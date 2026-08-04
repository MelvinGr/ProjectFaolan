using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Rdb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Rdb
{
	// Combo catalog from the client RDB type 1010206 ("Combo animation defs") - PLAINTEXT XML, so NO key is needed
	// (unlike the TEA-encrypted ability/item templates). Each entry is a <combo ...> element carrying the combo's name,
	// unlock LEVEL, featid (-> the class feat), timing (cooldown/stamina), the weapon criteria, and its input action
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
				row.Quality = (int)ParseUint(combo, "quality");
				row.Animation = (string?)combo.Attribute("animation") ?? "";
				row.Actions = string.Join(",", combo.Elements("action").Select(a => (string?)a.Attribute("name")).Where(n => !string.IsNullOrEmpty(n)));
				imported++;
			}

			await db.SaveChangesAsync();

			// #296: a combo's reuse time onto the SPELL row the combo drives. `Combos.Cooldown` is the XML's own
			// `cooldown` attribute in seconds; `Spell.RecastCentiseconds` is the client's centiseconds.
			//
			// This replaces an ellatha scrape that matched by DISPLAY NAME, and the name is exactly what it got wrong:
			// several combos share one display name (`Enraging Strike III` is a 1h and a polearm variant) and the
			// scrape wrote one value across every id-variant. Joining on `GameActionId` is per-combo and per-id.
			//
			// ⚠️ The two recast sources are DISJOINT, and that is measured, not assumed: of the 246 combo spells
			// carrying an XML cooldown, ZERO are named by a `SetSpellRecastTime` element and ZERO sit on a stacking
			// line a `SetSpellLineRecastTime` element targets (#271). A combo's reuse time and a spell's recast are
			// two separate facts in two separate places, so neither pass can overwrite the other's answer.
			//
			// EVERY combo, not only the ones carrying a cooldown: the XML is the sole authority here, so a stale
			// value left behind by the retired scrape has to go back to 0 rather than survive as the last thing that
			// wrote it.
			//
			// ⚠️ MAX across the combos sharing a game action, not last-write-wins. One ability is driven by several
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

			await db.SaveChangesAsync();
			logger?.LogInformation("RdbComboImporter: {Seen} combo XML entries parsed, {Imported} combos imported, {Recasts} combo recasts set on their spell rows ({Cleared} cleared).", seen, imported, recastsSet, recastsCleared);
			return (seen, imported);
		}

		private static uint ParseUint(XElement el, string attr)
		{
			return el.Attribute(attr) is { Value: var v } && uint.TryParse(v, out var n)
				? n
				: 0u;
		}
	}
}
