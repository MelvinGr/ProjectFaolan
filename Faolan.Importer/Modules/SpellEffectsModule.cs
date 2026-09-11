using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Importer.Cli;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Modules
{
	// The server-acted spell effects (SpellEffect): what a spell makes the server do, beyond the generic
	// ApplySpell + duration + stat deltas the wire path already covers.
	//
	// These are decoded facts, not extractable rows - each one is a spell id paired with an observed consequence
	// (a creature that spawned, a buff that landed), read out of the captures by hand and cited below. They live in
	// the database rather than as hardcoded id checks in the server, so the cast path dispatches on data and a new
	// summon or proc is a row here.
	//
	// A seeded effect's spell must already be in the catalog (SpellEffect.SpellId is an FK to Spells) - the module
	// checks that up front and fails naming the ids, rather than writing rows that point at nothing.
	public sealed class SpellEffectsModule : IImportModule
	{
		// The pet-summon roster is read from the catalog, not listed here. A hand-typed list of (spellId, template,
		// combat) triples lifted out of one capture fails silently for every summon it does not know about, and the
		// RDB names 319 summon spells - including combat pets of the very class such a capture would be of.
		//
		// Each of the three facts now comes from the record that states it:
		//   * which spells summon - `Spell.SummonSpawnHash != null`, off the ScriptParams of the spell's own
		//     server-side-script GameAction element (SummonParams).
		//   * whether the pet fights - `Spell.SummonIsCompanion`, off which of the two param spellings that string
		//     uses. Every captured summon agrees with the wire's own PetIIR kind triple.
		//   * which creature - the spell's name. Retail names a summon after its pet: `Undead Minion: Corruptor`,
		//     `Companion: Pleasurable Company`, `Pet: King Cobra`. Taken as the part after the first ": " (the whole
		//     name when there is none) and accepted only when it matches an NpcTemplates row, which is also the only
		//     thing the runtime can actually spawn.
		//
		// The spawnHash CORROBORATES the naming rule but cannot replace it: spells sharing a pet share a hash
		// (`GGDI` on all three Mutilator spells, `NSAO` on both Corruptor ones), so a hash whose spells disagreed on
		// the creature would be visible. It resolves to nothing in the client - see Spell.SummonSpawnHash.

		// Frozen Hatred's fatality proc. The trigger is the buff 4377209 - the effect record the castable
		// 4377207 points at - being live on the killer; retail's own words for it are that spell's description (LDB
		// 50211): "When you or your group perform a fatality and this buff is active, the Frozen Hatred pet appears
		// and is under your control for 60 seconds. If you already have a Frozen Hatred pet, the duration of the pet
		// will increase by another 60 seconds. This timer is capped at 5 minutes."
		//
		// The wire half is in the corpus: 21 of the 36 captured fatality bursts carry a third ApplySpell, 3666099
		// "Frozen Hatred" self, 6s, riding the same frame as the two Fatality ids
		// (xxx_mage-necromancer beach->city fr1267). The pet is NPC template "Frozen Hatred" (14580).
		private const uint FrozenHatredBuff = 4377209;
		private const uint FrozenHatredProc = 3666099;

		public IReadOnlyList<string> DependsOn => [];

		// In the default set despite needing no captures: these rows are core content, and a database without them
		// silently loses pet summons (SpellEffectService warns at load, but the fix belongs here).
		public bool InDefaultSet => true;

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert,
			WriteMode.Overwrite
		};

		// Needs only the database - the facts are decoded, not extracted, so this module runs without a captures dir.
		public ImportSource Source => ImportSource.Db;

		public string Description => "Server-acted spell effects: pet summons + the fatality proc (#307/#140).";
		public string Name => "spell-effects";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			// the summon roster comes out of the catalog the `spells` module built (Spell.SummonSpawnHash), so
			// this module still needs nothing but the database - it reads a column instead of carrying a list.
			var summonSpells = await ctx.Db.Spells.Where(s => s.SummonSpawnHash != null).ToListAsync();
			var templateNames = (await ctx.Db.NpcTemplates.Select(t => t.Name).Distinct().ToListAsync())
				.GroupBy(n => n.ToLowerInvariant())
				.ToDictionary(g => g.Key, g => g.First());
			var (summons, unresolved) = BuildSummonRows(summonSpells, templateNames);

			// A catalog with no summon params at all means `spells` has not been run against an RDB that carries them
			// - which would silently drop every pet summon, the exact failure this ticket replaced a hand-typed list
			// to avoid. Say so instead.
			if (summonSpells.Count == 0)
				throw new InvalidOperationException("spell-effects: no spell in the catalog carries summon params - run `rdb-spells` (with --Content:RdbDir) first, or the pet roster would come out empty.");

			var rows = summons.Concat(BuildRows()).ToList();
			var mode = ctx.ModeOverrides.GetValueOrDefault(Name, ctx.Mode);

			// Every spell an effect names must be in the catalog - SpellEffect.SpellId is an FK to Spells, and a
			// TargetSpellId the catalog lacks is a spell the server would apply blind (no name, no duration). All 14
			// are there today; if one ever is not, that is a seed typo or a short catalog, and both want saying out
			// loud rather than a nameless row minted underneath them.
			var ids = rows.Select(r => r.SpellId).Concat(rows.Select(r => r.TargetSpellId ?? 0)).Where(id => id != 0).Distinct().ToList();
			var cataloged = (await ctx.Db.Spells.Where(s => ids.Contains(s.Id)).Select(s => s.Id).ToListAsync()).ToHashSet();
			var unknown = ids.Where(id => !cataloged.Contains(id)).ToList();
			if (unknown.Count > 0)
				throw new InvalidOperationException($"spell-effects: {unknown.Count} spell id(s) named by the seed are not in the catalog: {string.Join(", ", unknown.Select(id => $"0x{id:X8}"))}. Run `spells` first.");

			if (mode == WriteMode.Overwrite)
				await ctx.Db.SpellEffects.ExecuteDeleteAsync();

			var existing = mode == WriteMode.Overwrite
				? []
				: await ctx.Db.SpellEffects.ToDictionaryAsync(e => (e.SpellId, e.Trigger, e.Sequence));

			int inserted = 0, updated = 0;
			foreach (var row in rows)
			{
				if (!existing.TryGetValue((row.SpellId, row.Trigger, row.Sequence), out var current))
				{
					ctx.Db.SpellEffects.Add(row);
					inserted++;
					continue;
				}

				if (mode == WriteMode.Insert)
					continue;

				current.Kind = row.Kind;
				current.TargetSpellId = row.TargetSpellId;
				current.Template = row.Template;
				current.Combat = row.Combat;
				current.DurationMs = row.DurationMs;
				current.MaxDurationMs = row.MaxDurationMs;
				updated++;
			}

			await ctx.Db.SaveChangesAsync();
			return new ImportResult($"{rows.Count} spell effect(s): {inserted} new, {updated} updated; " +
				$"{summons.Count} of {summonSpells.Count} catalogued summons resolved to a creature we hold ({unresolved} unresolved, #640).");
		}

		// Every summonable creature type name, lower-cased for matching - what the PETS module lifts onto its types.
		// Read from the seeded rows, the one place that knows which creatures a spell can put in play.
		public static async Task<IReadOnlySet<string>> PetTemplateNames(IDatabaseContext db)
		{
			var names = await db.SpellEffects.Where(e => e.Kind == SpellEffectKind.SummonPet && e.Template != null).Select(e => e.Template!).ToListAsync();
			return names.Select(n => n.ToLowerInvariant()).ToHashSet();
		}

		// The narrower roster the NPCS module excludes from world PLACEMENTS: creatures that exist only because a
		// player summoned them. It is deliberately not `PetTemplateNames`.
		//
		// A creature can be both placed in the world and summonable, and retail says which by how it names the
		// spell. `PetNameOf` already leans on that convention to read the name at all - these are its prefixes:
		//
		//   Pet: · Undead Minion: · Summon Horde: · Deathless Acolyte: · Dread Archmage: · (bare `Demon *`)
		//        combat pets - they exist only while summoned, and must never become scenery
		//   Companion:
		//        a vanity follower modelled on an NPC that stands in the world - `Companion: Courtesan`,
		//        `Companion: Drunk Pirate`, `Companion: Snake Charmer`, `Companion: Wolf Pict Warrior`
		//
		// `Companion: Casilda` is the one that cost us: the beach's chained Casilda is the NPC the whole Tortage
		// rescue chain hangs off, and the flat roster deleted her placement for 10 days because a
		// vanity spell shares her name. She is not a pet - freeing her makes her follow.
		//
		// A name is exempted only when every summon spell naming it is a `Companion:`. `Chicken` is both
		// `Companion: Chicken` and `Pet: Chicken`, so it stays excluded - one vanity spell cannot vouch for a
		// creature a hunter also summons.
		//
		// Residual gap, stated rather than hidden: this still excludes the placed `Pet:` animals (Chicken, Sheep,
		// Panther, Cobra, Rocknose stand in the world too). Separating those needs per-spawn evidence - the
		// `SummonedHandles` owner-binding - not a type-level roster, and the corpus has no PetIIR for them.
		public static async Task<IReadOnlySet<string>> SummonOnlyCreatureNames(IDatabaseContext db)
		{
			var summons = await db.SpellEffects
				.Where(e => e.Kind == SpellEffectKind.SummonPet && e.Template != null)
				.Join(db.Spells, e => e.SpellId, s => s.Id, (e, s) => new { e.Template, SpellName = s.Name })
				.ToListAsync();

			return summons
				.GroupBy(x => x.Template!.ToLowerInvariant())
				.Where(g => g.Any(x => !(x.SpellName ?? string.Empty).StartsWith("Companion: ", StringComparison.Ordinal)))
				.Select(g => g.Key)
				.ToHashSet();
		}

		// The pet a summon spell is named after: the part of the spell name after the first ": ", or the whole name
		// when it carries none. Retail's own convention across all 319 summon spells - `Undead Minion: Corruptor`,
		// `Pet: King Cobra`, `Mini-Pet: Vistrix`.
		public static string PetNameOf(string spellName)
		{
			var split = spellName.IndexOf(": ", StringComparison.Ordinal);
			return split < 0
				? spellName.Trim()
				: spellName[(split + 2)..].Trim();
		}

		// The summon rows the catalog yields: every spell carrying summon params whose name resolves to a creature we
		// hold a template for. A summon whose pet we have never seen is skipped and COUNTED - the runtime spawns by
		// template name, so a row naming a creature no NpcTemplate matches could not summon anything anyway, and the
		// count is the honest coverage number (~30 of 319) rather than a silence.
		// `templateNames` maps a lower-cased creature name to the template's own spelling, and the row stores the
		// latter: the runtime spawns by looking the name up, and retail's spell name is not always capitalised the
		// way its creature is (`Undead Minion: Life-Stealer` summons `Life-stealer`).
		public static (List<SpellEffect> Rows, int Unresolved) BuildSummonRows(IEnumerable<Spell> summonSpells, IReadOnlyDictionary<string, string> templateNames)
		{
			var rows = new List<SpellEffect>();
			var unresolved = 0;
			foreach (var spell in summonSpells)
			{
				var pet = PetNameOf(spell.Name ?? "");
				if (pet.Length == 0 || !templateNames.TryGetValue(pet.ToLowerInvariant(), out var template))
				{
					unresolved++;
					continue;
				}

				rows.Add(new SpellEffect
				{
					SpellId = spell.Id,
					Trigger = SpellEffectTrigger.OnCast,
					Sequence = 0,
					Kind = SpellEffectKind.SummonPet,
					Template = template,
					Combat = !spell.SummonIsCompanion
				});
			}

			return (rows, unresolved);
		}

		// The rows that are not read from the catalog. One effect, and it is not a summon spell: the Frozen Hatred
		// fatality proc, whose trigger is a buff being live rather than a cast.
		//
		// Its two durations are the only numbers in this module that are not harvested, and they are not
		// harvestable: they come from retail's own prose - the LDB 50211 description quoted above - not from an
		// observed timer. The corpus catches the proc firing but never times it out. Left as literals deliberately,
		// and said so here rather than sitting silently beside data that is read.
		public static IReadOnlyList<SpellEffect> BuildRows()
		{
			var rows = new List<SpellEffect>
			{
				// Both halves of the proc hang off the buff that arms it, in the order the capture shows them: the 6s
				// self-buff first (it rides the fatality burst's own frame), then the pet.
				new SpellEffect
				{
					SpellId = FrozenHatredBuff,
					Trigger = SpellEffectTrigger.OnFatality,
					Sequence = 0,
					Kind = SpellEffectKind.ApplySpell,
					TargetSpellId = FrozenHatredProc
				},
				new SpellEffect
				{
					SpellId = FrozenHatredBuff,
					Trigger = SpellEffectTrigger.OnFatality,
					Sequence = 1,
					Kind = SpellEffectKind.SummonPet,
					Template = "Frozen Hatred",
					Combat = true,
					DurationMs = 60_000,
					MaxDurationMs = 300_000
				}
			};

			return rows;
		}
	}
}
