using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Tsv
{
	// Fills the ClassSpell access map (which class may cast which spell) from the AoC fandom wiki's per-class spell
	// categories (Category:<Class> Spells), scraped to Captures/aoc_wiki_class_spells.tsv (classId, className,
	// spellName) - #296. This is the broad membership source; the create-capture module only ever had the ~7 beach
	// starters. Captures can't supply it (a char's capture shows only a handful of its spells), and the client RDB
	// spell record has no class field (class progression is server-side), so the community wiki is the source.
	//
	// The wiki lists BASE spell names ("Flesh to Worms"); our Spells catalog carries per-RANK rows
	// ("Flesh to Worms (Rank 1..N)") across the two RDB id-ranges. So a wiki name maps to EVERY rank row of that base
	// (a class has access to all ranks; the per-rank UNLOCK level rides Spells.Level). Match is rank-stripped,
	// case-insensitive, after dropping the wiki's " (spell)"/"(ability)"/... disambiguators. Idempotent (skips
	// (Class, SpellId) pairs already present, so it composes with the create-capture class-spells seed).
	public static class WikiClassSpellImporter
	{
		private static readonly Regex RankSuffix = new(@"\s*\(Rank \d+\)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
		private static readonly Regex Disambiguator = new(@"\s*\((spell|ability|combo|buff|debuff)\)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		private static string BaseKey(string name) => RankSuffix.Replace(Disambiguator.Replace(name, ""), "").Trim().ToLowerInvariant();

		public static async Task<(int Rows, int Matched, int Inserted)> ImportAsync(IDatabaseContext db, string tsvPath, ILogger logger = null)
		{
			if (!File.Exists(tsvPath))
			{
				logger?.LogWarning("WikiClassSpellImporter: {Path} not found - skipping class<->spell fill.", tsvPath);
				return (0, 0, 0);
			}

			// (classId, base-name-key) from the TSV.
			var wanted = new List<(uint Class, string Key)>();
			foreach (var line in await File.ReadAllLinesAsync(tsvPath))
			{
				if (line.Length == 0 || line[0] == '#')
					continue;
				var c = line.Split('\t');
				if (c.Length < 3 || !uint.TryParse(c[0], out var classId))
					continue;
				wanted.Add((classId, BaseKey(c[2])));
			}

			// base-name-key -> the ids of every rank row of that base.
			var byBase = new Dictionary<string, List<uint>>();
			foreach (var s in await db.Spells.Where(s => s.Name != null && s.Name != "").Select(s => new { s.Id, s.Name }).ToListAsync())
			{
				var key = BaseKey(s.Name);
				(byBase.TryGetValue(key, out var ids) ? ids : byBase[key] = []).Add(s.Id);
			}

			var existing = (await db.ClassSpells.Select(cs => new { cs.Class, cs.SpellId }).ToListAsync())
				.Select(x => ((uint)x.Class, x.SpellId)).ToHashSet();

			int matched = 0, inserted = 0;
			foreach (var (cls, key) in wanted)
			{
				if (!byBase.TryGetValue(key, out var ids))
					continue;
				matched++;
				foreach (var id in ids)
				{
					if (!existing.Add((cls, id)))
						continue;
					db.ClassSpells.Add(new ClassSpell { Class = (Class)cls, SpellId = id });
					inserted++;
				}
			}

			await db.SaveChangesAsync();
			logger?.LogInformation("WikiClassSpellImporter: {Rows} wiki rows, {Matched} matched a spell, {Inserted} new ClassSpell rows.", wanted.Count, matched, inserted);
			return (wanted.Count, matched, inserted);
		}
	}
}
