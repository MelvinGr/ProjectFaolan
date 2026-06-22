using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer
{
	// Backfills Spell.Level from the scraped ellatha spell list (Captures/ellatha_spells.tsv) - the only text source
	// for a spell's per-rank unlock level, since the level is NOT in the client RDB spell record (it's class-
	// progression data; see Captures/scripts/decode_spell_fields.py). LEVEL ONLY: ellatha's mana/cast are unreliable
	// (Chill rank 5 lists mana 107 vs the in-game 417), so this never touches other columns.
	//
	// Match is the exact "<name> (Rank <rank>)" form (the dominant DB naming). Base-name-only rows (the DB has the
	// spell without a rank suffix) are AMBIGUOUS across ranks and skipped. A spell name maps to >1 DB row (the two
	// RDB id-ranges), so every matching row gets the level. Idempotent.
	public static class EllathaSpellLevelImporter
	{
		public static async Task<(int Rows, int Matched, int Updated)> ImportAsync(IDatabaseContext db, string tsvPath, ILogger logger = null)
		{
			if (!File.Exists(tsvPath))
			{
				logger?.LogWarning("EllathaSpellLevelImporter: {Path} not found - skipping level backfill.", tsvPath);
				return (0, 0, 0);
			}

			// name -> level, keyed by the exact DB name form "<name> (Rank <rank>)".
			var levelByName = new Dictionary<string, byte>();
			var rows = 0;
			foreach (var line in await File.ReadAllLinesAsync(tsvPath))
			{
				if (line.Length == 0 || line[0] == '#')
					continue;

				var c = line.Split('\t');
				if (c.Length < 3 || !int.TryParse(c[1], out var rank) || !int.TryParse(c[2], out var level))
					continue;

				rows++;
				if (level is > 0 and <= byte.MaxValue)
					levelByName[$"{c[0]} (Rank {rank})"] = (byte)level;
			}

			// Load only the spells whose name we have a level for, then set it.
			var names = levelByName.Keys.ToList();
			var spells = await db.Spells.Where(s => names.Contains(s.Name)).ToListAsync();
			var matched = 0;
			var updated = 0;
			foreach (var spell in spells)
			{
				matched++;
				var level = levelByName[spell.Name];
				if (spell.Level != level)
				{
					spell.Level = level;
					updated++;
				}
			}

			await db.SaveChangesAsync();
			logger?.LogInformation("EllathaSpellLevelImporter: {Rows} ellatha rows, {Matched} DB spells matched, {Updated} levels set.", rows, matched, updated);
			return (rows, matched, updated);
		}
	}
}
