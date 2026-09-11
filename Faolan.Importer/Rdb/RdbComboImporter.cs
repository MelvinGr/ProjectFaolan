using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Rdb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Rdb
{
	// Combo catalog from the client RDB type 1010206 ("Combo animation defs") - plaintext XML, so no key is needed
	// (unlike the TEA-encrypted ability/item templates). Each entry is a <combo...> element carrying the combo's name,
	// unlock level, featid (-> the class feat), timing (cooldown/stamina), the weapon criteria, and its input action
	// sequence. Keyed by the RDB entry id = the combo id (the same id ComboBookIIR grants and CharacterCombo stores).
	// The catalog counterpart of Spell / Ability.
	public static class RdbComboImporter
	{
		private const uint ComboXmlType = 1010206;

		public static async Task<(int Seen, int Imported)> ImportAsync(IDatabaseContext db, string rdbDir, string? cdnBase = null, ILogger? logger = null)
		{
			using var rdb = new RdbReader(rdbDir, cdnBase);
			var existing = await db.Combos.ToDictionaryAsync(c => c.Id);

			var seen = 0;
			var imported = 0;
			foreach (var e in rdb.EntriesOfType(ComboXmlType))
			{
				if (!rdb.CanRead(e)) // CDN-only with no reachable mirror / a negative-cached miss
					continue;

				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue;
				}

				if (body.Length == 0) // CDN miss (404 / proxy down)
					continue;

				var xml = RdbXml.Carve(Encoding.UTF8.GetString(body), "combo");
				if (xml == null)
					continue;

				XElement combo;
				try
				{
					combo = XElement.Parse(xml);
				}
				catch
				{
					continue;
				}

				seen++;
				if (!existing.TryGetValue(e.Id, out var row))
				{
					row = new Combo
					{
						Id = e.Id
					};
					db.Combos.Add(row);
					existing[e.Id] = row;
				}

				row.Name = (string?)combo.Attribute("name") ?? "";
				row.Level = (byte)ParseUint(combo, "level");
				var featId = ParseUint(combo, "featid");
				row.FeatId = featId == 0
					? null
					: featId;
				row.ParentId = ParseUint(combo, "parentid");
				row.GameActionId = ParseUint(combo, "gameactionid");
				row.Cooldown = (int)ParseUint(combo, "cooldown");
				row.Stamina = (int)ParseUint(combo, "stamina");
				row.Criteria = (int)ParseUint(combo, "criteria");
				// Signed, and the sign carries meaning: `-1` is the generic set. So this is parsed rather than run
				// through `ParseUint`, which would fold it to 0 and make a generic row look like class 0.
				row.NpcClass = int.TryParse((string?)combo.Attribute("npcclass"), out var npcClass) ? npcClass : null;
				row.Quality = (int)ParseUint(combo, "quality");
				row.Animation = (string?)combo.Attribute("animation") ?? "";
				row.Actions = string.Join(",", combo.Elements("action").Select(a => (string?)a.Attribute("name")).Where(n => !string.IsNullOrEmpty(n)));
				imported++;
			}

			await db.SaveChangesAsync();

			// a combo's reuse time onto the spell row the combo drives. `Combos.Cooldown` is the XML's own
			// `cooldown` attribute in seconds; `Spell.RecastCentiseconds` is the client's centiseconds.
			//
			// This replaces an ellatha scrape that matched by display name, and the name is exactly what it got wrong:
			// several combos share one display name (`Enraging Strike III` is a 1h and a polearm variant) and the
			// scrape wrote one value across every id-variant. Joining on `GameActionId` is per-combo and per-id.
			//
			// The two recast sources are DISJOINT, and that is measured, not assumed: of the 246 combo spells
			// carrying an XML cooldown, zero are named by a `SetSpellRecastTime` element and zero sit on a stacking
			// line a `SetSpellLineRecastTime` element targets. A combo's reuse time and a spell's recast are
			// two separate facts in two separate places, so neither pass can overwrite the other's answer.
			//
			// every combo, not only the ones carrying a cooldown: the XML is the sole authority here, so a stale
			// value left behind by the retired scrape has to go back to 0 rather than survive as the last thing that
			// wrote it.
			//
			// MAX across the combos sharing a game action, not last-write-wins. One ability is driven by several
			// combo rows - `Enraging Wound I` (166263) by its 1h, polearm and 2h variants, all stating 12 s - and a
			// MONSTER combo can reuse the same game action while stating none (`Ghauhk_eatface`, `monster_snare`).
			// A 0 there means that row does not state a cooldown, not that the ability has none, so it must not win.
			var byGameAction = existing.Values
				.GroupBy(c => c.GameActionId)
				.ToDictionary(g => g.Key, g => (uint)g.Max(c => c.Cooldown) * 100);
			var targets = byGameAction.Keys.ToList();
			var recastsSet = 0;
			var recastsCleared = 0;
			foreach (var spell in await db.Spells.Where(s => targets.Contains(s.Id)).ToListAsync())
			{
				var recast = byGameAction[spell.Id];
				if (spell.RecastCentiseconds == recast)
					continue;

				if (recast == 0)
					recastsCleared++;
				else
					recastsSet++;

				spell.RecastCentiseconds = recast;
			}

			var levelsSet = await FillComboSpellLevels(db, existing.Values);
			var classCombos = await SyncClassCombos(db, existing.Values, logger);

			logger?.LogInformation(
				"RdbComboImporter: {Seen} combo XML entries parsed, {Imported} combos imported, {Recasts} combo recasts set on their spell rows ({Cleared} cleared), {Levels} combo spell levels sourced, {ClassCombos} class<->combo rows.",
				seen, imported, recastsSet, recastsCleared, levelsSet, classCombos);
			return (seen, imported);
		}

		// The UNLOCK level of a combo's spell, from the combo XML - the second re-derivable source for `Spells.Level`
		// .
		//
		// The spell record (RDB 1040005 field 125) carries a level for 7,378 of the shipped 8,221, and the remaining
		// 843 came from the retired ellatha/wiki scrapes: delete the database and re-import, and those come back at 0,
		// which matters because `Level` is the gate `GetClassSpellsUnlockedBetween` queries. 251 of the 843 are combo
		// spells, and the combo XML states their level outright.
		//
		// `level="1"` is a SENTINEL, not level one. Every combo the client ships as a generic effect template
		// (`generic_DOT_fire_10sec`, `generic_stun_3_sec`,...) sits at 1, and those are exactly the 8 rows where the
		// combo level contradicts a spell record that does state one - so excluding them makes the two RDB sources
		// disjoint rather than merely mostly-agreeing, and the combo XML can be authoritative for what it covers.
		//
		// The column is cross-checked against the client's own combo names, which embed the level
		// (`BearShaman_15_hitdebuff_Ursine_Brawl_I`, `Herald_24_dot_Burn_to_Death_II`): 431 of 431 names that carry a
		// number agree with it. That is also what settles the five places where it contradicts a scraped value - the
		// name backs the XML every time, so the scrape was simply wrong (`Overreach IV` is level 56, not 80).
		//
		// MIN across the combos sharing a game action, not MAX: it is the first level at which any variant of the
		// combo can be used. Only one game action in the shipped data has two (3180972 at 60 and 70).
		private static async Task<int> FillComboSpellLevels(IDatabaseContext db, IEnumerable<Combo> combos)
		{
			var byGameAction = combos
				.Where(c => c.Level > 1)
				.GroupBy(c => c.GameActionId)
				.ToDictionary(g => g.Key, g => (byte)g.Min(c => c.Level));

			var targets = byGameAction.Keys.ToList();
			var set = 0;
			foreach (var spell in await db.Spells.Where(s => targets.Contains(s.Id)).ToListAsync())
			{
				var level = byGameAction[spell.Id];
				if (spell.Level == level)
					continue;

				spell.Level = level;
				set++;
			}

			return set;
		}

		/// <summary>
		///     Fill the class↔combo map from the stacking line of the spell each combo drives. A combo's
		///     <c>GameActionId</c> is a spell id, and a spell's <c>GuiStackingLine</c> encoded as
		///     <c>ConanClass * 1000 + lineIndex</c> names its class - so the client answers "which class gets this
		///     combo" for the feat-tree prefixes (<c>Reaver_</c>, <c>Liberator_</c>, <c>Halberdier_</c>, <c>Lich_</c>)
		///     that the name heuristic in <c>CaptureAbilityImporter</c> deliberately refuses to guess at.
		/// </summary>
		/// <remarks>
		///     <b>Only lineIndex ≥ 400.</b> The archetype-shared combos sit in a low block that reads as class 20
		///     and is not one: <c>Soldier_</c> on 20002-20005, <c>Rogue_</c> on 20006-20007, <c>Priest_</c> on 20008 -
		///     three different archetypes under the same "20", so the number is a block, not a class. Taking it at
		///     face value files every Rogue and Priest archetype combo under Guardian. The split is exceptionless over
		///     all 17 name prefixes in the catalog: every class-specific and feat-tree combo is at 401+
		///     (Barbarian 18401-18409, Guardian 20401-20406, Halberdier 20407-20410, Ranger 39401-39412, …) and every
		///     archetype-shared one is at ≤ 8. Which class each archetype combo also belongs to needs the captured
		///     action bars.
		///     <para>
		///         Corroborated where it can be: over the combos the existing name-prefix + action-bar map already
		///         covers, this agrees on <b>257 of 257</b>, with no disagreements.
		///     </para>
		///     <para>
		///         NPC/monster combos are excluded by name, as they already are in <c>CaptureAbilityImporter</c>.
		///         Nearly all of them carry no stacking line at all, but a handful reuse a player spell's game action
		///         (<c>generic_DOT</c> drives the Conqueror's <c>Bloody Hack II</c>), and those would otherwise arrive
		///         as a real class row.
		///     </para>
		/// </remarks>
		private static async Task<int> SyncClassCombos(IDatabaseContext db, IEnumerable<Combo> combos, ILogger? logger)
		{
			// The lowest lineIndex any class-specific or feat-tree combo uses; everything below is the shared
			// archetype block. See the remarks - this is measured over the whole catalog, not chosen.
			const uint firstClassLineIndex = 400;

			var lineBySpell = await db.Spells.Where(s => s.GuiStackingLine > 0).ToDictionaryAsync(s => s.Id, s => s.GuiStackingLine);
			var known = (await db.ClassCombos.ToListAsync()).ToHashSet(new ClassComboKey());
			var added = 0;
			var conflicts = 0;

			foreach (var combo in combos)
			{
				if (NpcComboPrefixes.Any(p => combo.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
					continue;

				if (!lineBySpell.TryGetValue(combo.GameActionId, out var line) || line % 1000 < firstClassLineIndex)
					continue;

				if (!Enum.IsDefined((ConanClass)(line / 1000)))
					continue;

				var row = new ClassCombo
				{
					ComboId = combo.Id,
					Class = (ConanClass)(line / 1000),
					Level = combo.Level
				};
				if (!known.Add(row))
					continue;

				// A combo already mapped to a different class is worth saying out loud: one stacking line means one
				// class, so this is a disagreement to look at rather than a combo two classes share.
				if (known.Any(k => k.ComboId == row.ComboId && k.Class != row.Class))
					conflicts++;

				db.ClassCombos.Add(row);
				added++;
			}

			await db.SaveChangesAsync();
			logger?.LogInformation("RdbComboImporter: {Added} class<->combo rows from the driven spell's stacking line ({Conflicts} on a combo another class already claims).", added, conflicts);
			return added;
		}

		// NPC/monster/generic combo name prefixes - not player combos, and excluded here for the same reason
		// CaptureAbilityImporter excludes them.
		private static readonly string[] NpcComboPrefixes = ["combo_npc_", "monster_", "generic_", "Ghauhk_"];

		// (Class, ComboId) identity - the table's own primary key - so an existing row is recognised regardless of
		// the Level it carries.
		private sealed class ClassComboKey : IEqualityComparer<ClassCombo>
		{
			public bool Equals(ClassCombo? a, ClassCombo? b)
			{
				return a?.ComboId == b?.ComboId && a?.Class == b?.Class;
			}

			public int GetHashCode(ClassCombo x)
			{
				return HashCode.Combine(x.ComboId, x.Class);
			}
		}

		private static uint ParseUint(XElement el, string attr)
		{
			return el.Attribute(attr) is { Value: var v } && uint.TryParse(v, out var n)
				? n
				: 0u;
		}
	}
}
