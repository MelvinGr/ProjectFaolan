using System;
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

		public static async Task<(int Seen, int Imported)> ImportAsync(IDatabaseContext db, string rdbDir, ILogger logger = null)
		{
			using var rdb = new RdbReader(rdbDir);
			var existing = await db.Combos.ToDictionaryAsync(c => c.Id);

			var seen = 0;
			var imported = 0;
			foreach (var e in rdb.EntriesOfType(ComboXmlType).Where(x => x.File != 0xff))
			{
				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue;
				}

				// The entry is an XML doc after the RDB-Id header; carve out just the <combo>...</combo> root (the body
				// can carry a header before it and padding after it, which would trip a strict parse).
				var text = Encoding.UTF8.GetString(body);
				var start = text.IndexOf("<combo", StringComparison.Ordinal);
				var end = text.IndexOf("</combo>", StringComparison.Ordinal);
				if (start < 0 || end < 0)
					continue;

				XElement combo;
				try
				{
					combo = XElement.Parse(text.Substring(start, end - start + "</combo>".Length));
				}
				catch
				{
					continue;
				}

				seen++;
				if (!existing.TryGetValue(e.Id, out var row))
				{
					row = new Combo { Id = e.Id };
					db.Combos.Add(row);
					existing[e.Id] = row;
				}

				row.Name = (string)combo.Attribute("name") ?? "";
				row.Level = (byte)ParseUint(combo, "level");
				row.FeatId = ParseUint(combo, "featid");
				row.ParentId = ParseUint(combo, "parentid");
				row.GameActionId = ParseUint(combo, "gameactionid");
				row.Cooldown = (int)ParseUint(combo, "cooldown");
				row.Stamina = (int)ParseUint(combo, "stamina");
				row.Criteria = (int)ParseUint(combo, "criteria");
				row.Quality = (int)ParseUint(combo, "quality");
				row.Animation = (string)combo.Attribute("animation") ?? "";
				row.Actions = string.Join(",", combo.Elements("action").Select(a => (string)a.Attribute("name")).Where(n => !string.IsNullOrEmpty(n)));
				imported++;
			}

			await db.SaveChangesAsync();
			logger?.LogInformation("RdbComboImporter: {Seen} combo XML entries parsed, {Imported} combos imported.", seen, imported);
			return (seen, imported);
		}

		private static uint ParseUint(XElement el, string attr)
		{
			return el.Attribute(attr) is { Value: var v } && uint.TryParse(v, out var n) ? n : 0u;
		}
	}
}
