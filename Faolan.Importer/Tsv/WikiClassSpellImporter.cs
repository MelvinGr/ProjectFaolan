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
		private static readonly Regex Disambiguator = new Regex(@"\s*\((spell|ability|combo|buff|debuff)\)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
		private static readonly Regex RankSuffix = new Regex(@"\s*\(Rank \d+\)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		public static async Task<(int Rows, int Matched, int Inserted)> ImportAsync(IDatabaseContext db, string tsvPath, ILogger? logger = null)
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

			// base-name-key -> ONE id per rank of that base. A logical spell exists as multiple catalog ids (the two
			// RDB id-ranges: one is the real castable record with CastTime > 0, the other an instant/free variant with
			// CastTime 0). Keep ONE id per full (rank-specific) NAME - the real cast (CastTime > 0), tie-break lowest id
			// - so a class never learns the same spell twice or gets handed the wrong instant variant.
			var reps = (await db
					.Spells
					.Where(s => s.Name != null && s.Name != "")
					.Select(s => new
					{
						s.Id,
						s.Name,
						s.CastTime
					})
					.ToListAsync())
				.GroupBy(s => s.Name)
				.Select(g => g.OrderByDescending(x => x.CastTime > 0).ThenBy(x => x.Id).First());
			var byBase = new Dictionary<string, List<uint>>();
			foreach (var s in reps)
			{
				var key = BaseKey(s.Name);
				(byBase.TryGetValue(key, out var ids)
					? ids
					: byBase[key] = []).Add(s.Id);
			}

			var existing = (await db
					.ClassSpells
					.Select(cs => new
					{
						cs.Class,
						cs.SpellId
					})
					.ToListAsync())
				.Select(x => ((uint)x.Class, x.SpellId))
				.ToHashSet();

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

					db.ClassSpells.Add(new ClassSpell
					{
						Class = (Class)cls,
						SpellId = id
					});
					inserted++;
				}
			}

			await db.SaveChangesAsync();

			// Final dedup: collapse any (Class, spell-NAME) that still has >1 catalog id (e.g. a create-capture starter
			// whose captured id differs from the wiki rep, or a name spanning both RDB id-ranges) to the real castable
			// record (CastTime > 0, tie lowest id) - so a class never learns the same spell twice / the instant variant.
			var joined = await db
				.ClassSpells
				.Join(db.Spells,
				cs => cs.SpellId,
				s => s.Id,
				(cs, s) => new
				{
					cs,
					s.Name,
					s.CastTime
				})
				.ToListAsync();
			var removed = joined.GroupBy(x => (x.cs.Class, x.Name)).Where(g => g.Count() > 1).SelectMany(g => g.OrderByDescending(x => x.CastTime > 0).ThenBy(x => x.cs.SpellId).Skip(1)).Select(x => x.cs).ToList();
			if (removed.Count > 0)
			{
				db.ClassSpells.RemoveRange(removed);
				await db.SaveChangesAsync();
			}

			logger?.LogInformation("WikiClassSpellImporter: {Rows} wiki rows, {Matched} matched a spell, {Inserted} new, {Deduped} dup ids dropped.", wanted.Count, matched, inserted, removed.Count);
			return (wanted.Count, matched, inserted);
		}

		// Backfills Spell.Level from the wiki per-class spell tables (aoc_wiki_spell_levels.tsv: spellName, level). The
		// wiki level is the BASE spell's attainable level, so it only sets the RANK-1 / unranked row (higher ranks carry
		// their own level from ellatha) and ONLY where the level is currently 0 (never overwrites ellatha's per-rank
		// data). Complements EllathaSpellLevelImporter for the spells ellatha's ranked list missed. Idempotent.
		public static async Task<(int Rows, int Updated)> ImportLevelsAsync(IDatabaseContext db, string tsvPath, ILogger? logger = null)
		{
			if (!File.Exists(tsvPath))
			{
				logger?.LogWarning("WikiClassSpellImporter: {Path} not found - skipping spell-level backfill.", tsvPath);
				return (0, 0);
			}

			var levelByBase = new Dictionary<string, byte>();
			var rows = 0;
			foreach (var line in await File.ReadAllLinesAsync(tsvPath))
			{
				if (line.Length == 0 || line[0] == '#')
					continue;

				var c = line.Split('\t');
				if (c.Length < 2 || !byte.TryParse(c[1], out var lvl) || lvl == 0)
					continue;

				rows++;
				levelByBase[BaseKey(c[0])] = lvl;
			}

			// Only the base/rank-1 rows currently at Level 0 (Name == base, or "base (Rank 1)").
			var candidates = await db.Spells.Where(s => s.Level == 0 && s.Name != null && s.Name != "" && (!s.Name.Contains("(Rank ") || s.Name.Contains("(Rank 1)"))).ToListAsync();
			var updated = 0;
			foreach (var s in candidates)
			{
				if (levelByBase.TryGetValue(BaseKey(s.Name), out var lvl))
				{
					s.Level = lvl;
					updated++;
				}
			}

			await db.SaveChangesAsync();
			logger?.LogInformation("WikiClassSpellImporter: {Rows} wiki level rows, {Updated} base spell levels set.", rows, updated);
			return (rows, updated);
		}

		private static string BaseKey(string name)
		{
			return RankSuffix.Replace(Disambiguator.Replace(name, ""), "").Trim().ToLowerInvariant();
		}
	}
}
