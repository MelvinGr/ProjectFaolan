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
	// The server-acted spell effects (SpellEffect): what a spell makes the SERVER do, beyond the generic
	// ApplySpell + duration + stat deltas the wire path already covers.
	//
	// These are decoded FACTS, not extractable rows - each one is a spell id paired with an observed consequence
	// (a creature that spawned, a buff that landed), read out of the captures by hand and cited below. They lived as
	// hardcoded id checks in the server (PetSummonCatalog, FatalityService's Frozen Hatred constants) until #439
	// moved them into the database, so the cast path can dispatch on DATA and a new summon or proc is a row here.
	//
	// A seeded effect's spell must already be in the catalog (SpellEffect.SpellId is an FK to Spells) - the module
	// checks that up front and fails naming the ids, rather than writing rows that point at nothing.
	public sealed class SpellEffectsModule : IImportModule
	{
		// The necro's summonable pets (#307, docs/subsystems/summon-pet-decode.md). Spell id = the CastSpell trailer
		// id; the template = the name of the FA015807 creature that spawned after the channel. The full roster is in
		// NecroL80_..._mount-horse_all-necro-summons_dismiss-all (all 12 summoned + mass-dismissed). "Pleasurable
		// Company" is the generic SOCIAL companion (non-combat, not targetable); everything else is a combat pet.
		private static readonly (uint Spell, string Template, bool Combat)[] PetSummons =
		[
			(0x004105E9, "Pleasurable Company", false), (0x0005E2F5, "Arcanist", true), (0x0005E2F0, "Magus", true), (0x0005E2F6, "Reaper", true), (0x0005E321, "Archmagus", true),
			(0x0005E322, "Blood Arcanist", true), (0x0004F813, "Corruptor", true), (0x0004F814, "Blighted One", true), (0x0004F815, "Necrotic Bomb", true), (0x0004F816, "Harvester", true),
			(0x0004F817, "Life-stealer", true), (0x00127EDD, "Mutilator", true)
		];

		// Frozen Hatred's fatality proc (#140/#434). The trigger is the buff 4377209 - the effect record the castable
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
			var rows = BuildRows();
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
			return new ImportResult($"{rows.Count} spell effect(s): {inserted} new, {updated} updated.");
		}

		// The summonable pet creature TYPE names, lower-cased for matching. Two importers need them: the pets module
		// lifts exactly these creatures onto their types, and the npcs module excludes them from the world PLACEMENTS
		// (a captured session's summoned pets are not scenery). Both read them from the seeded rows - the one place
		// that knows which creatures are pets.
		public static async Task<IReadOnlySet<string>> PetTemplateNames(IDatabaseContext db)
		{
			var names = await db.SpellEffects.Where(e => e.Kind == SpellEffectKind.SummonPet && e.Template != null).Select(e => e.Template!).ToListAsync();
			return names.Select(n => n.ToLowerInvariant()).ToHashSet();
		}

		// The seed, as a pure function so a test can assert the shipped rows against it.
		public static IReadOnlyList<SpellEffect> BuildRows()
		{
			var rows = PetSummons.Select(p => new SpellEffect
			{
				SpellId = p.Spell,
				Trigger = SpellEffectTrigger.OnCast,
				Sequence = 0,
				Kind = SpellEffectKind.SummonPet,
				Template = p.Template,
				Combat = p.Combat
			}).ToList();

			// Both halves of the proc hang off the buff that arms it, in the order the capture shows them: the 6s
			// self-buff first (it rides the fatality burst's own frame), then the pet.
			rows.Add(new SpellEffect
			{
				SpellId = FrozenHatredBuff,
				Trigger = SpellEffectTrigger.OnFatality,
				Sequence = 0,
				Kind = SpellEffectKind.ApplySpell,
				TargetSpellId = FrozenHatredProc
			});
			rows.Add(new SpellEffect
			{
				SpellId = FrozenHatredBuff,
				Trigger = SpellEffectTrigger.OnFatality,
				Sequence = 1,
				Kind = SpellEffectKind.SummonPet,
				Template = "Frozen Hatred",
				Combat = true,
				DurationMs = 60_000,
				MaxDurationMs = 300_000
			});

			return rows;
		}
	}
}
