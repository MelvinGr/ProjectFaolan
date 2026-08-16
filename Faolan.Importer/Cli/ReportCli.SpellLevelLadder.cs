using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Progression;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report spell-level-ladder`: which spell FAMILIES ship a per-rank level ladder, and which ship one level for
	// several ranks - so that a single ding teaches two, three or six ranks of the same spell at once.
	//
	// A family's ranks unlock at spaced levels (`Ice Strike` 7/20/35/50/65/80). `Spells.Level` comes from RDB field
	// 125, which states that ladder for most families and states NO ladder for a handful: every rank of `Chill` reads
	// 7, every rank of `Infestation` reads 2, `Lifestrike`'s five ranks read 2/3/4/4/4. The symptom is in-game and
	// loud - a level-4 Necromancer dinging once was handed Lifestrike ranks 3, 4 AND 5, the last of which states its
	// damage for levels 75-80.
	//
	// `RdbSpellImporter.RankLadderFromEffectBands` repairs the families where the record answers a second time (the
	// `InterpolatedHit` ramp's level band) completely and monotonically. It cannot repair the rest, and this is where
	// that residue is counted rather than left invisible: a family with no bands, with bands on only some ranks, or
	// with bands that step down or run past the level cap keeps field 125's answer, collapse and all.
	//
	// ⭐ **Why a report and not a test** (Gitea #729): both sides are shipped DATA. The count of collapsed families
	// moves whenever the RDB import is re-run or the catalog grows, so pinning it would rebaseline on every data
	// change. What is asserted is the one direction that can only be a defect - a family the importer's rule WOULD
	// separate but whose shipped rows are still collapsed, i.e. `faolan.db` predates the rule and the module needs
	// re-running.
	public static partial class ReportCli
	{
		private const string LadderRankSuffix = " (Rank ";

		private static async Task<int> SpellLevelLadder()
		{
			await using var db = OpenDb();

			var classSpells = (await db.ClassSpells.AsNoTracking().Select(cs => cs.SpellId).Distinct().ToListAsync()).ToHashSet();
			var comboActions = (await db.Combos.AsNoTracking().Select(c => c.GameActionId).ToListAsync()).ToHashSet();

			// ⚠️ The family is grouped over the WHOLE catalog, not over the class spells, because that is what the
			// importer's rule groups over - and the difference decides cases. `Covenant of Pestilence` has three
			// records per rank (the cast plus its companion effects) whose bands disagree, so the rule declines it;
			// scoped to the one class-spell record per rank it would look repairable and this report would promise a
			// re-import that changes nothing. Only the DISPLAY is narrowed, to the families a character can learn.
			var spells = await db.Spells.AsNoTracking()
				.Where(s => s.Level > 0)
				.Select(s => new
				{
					s.Id,
					s.Name,
					s.Level
				})
				.ToListAsync();

			var bands = await db.SpellStatEffects.AsNoTracking()
				.Where(e => e.LowLevel != null)
				.GroupBy(e => e.SpellId)
				.Select(g => new
				{
					SpellId = g.Key,
					Low = g.Max(e => e.LowLevel)
				})
				.ToDictionaryAsync(x => x.SpellId, x => (uint?)x.Low);

			var families = new Dictionary<string, List<(uint Id, string Name, byte Level, int Rank)>>(StringComparer.Ordinal);
			foreach (var spell in spells)
			{
				if (comboActions.Contains(spell.Id) || LadderRankOf(spell.Name) is not { } ranked)
					continue;

				(families.TryGetValue(ranked.Family, out var list)
					? list
					: families[ranked.Family] = []).Add((spell.Id, spell.Name, spell.Level, ranked.Rank));
			}

			var learnable = families.Where(f => f.Value.Any(s => classSpells.Contains(s.Id))).ToList();
			var collapsed = new List<(string Family, string Field, string Band, bool Repairable)>();
			foreach (var (family, list) in learnable.OrderBy(f => f.Key, StringComparer.Ordinal))
			{
				var byRank = list.GroupBy(s => s.Rank).OrderBy(g => g.Key).ToList();
				var fieldLevels = list.Select(s => (int)s.Level).Distinct().Count();
				if (fieldLevels >= byRank.Count)
					continue;

				var ladder = new List<uint>(byRank.Count);
				foreach (var rank in byRank)
				{
					var lows = rank.Select(s => bands.GetValueOrDefault(s.Id)).Distinct().ToList();
					if (lows is [{ } low] && low > 0 && low <= LevelingResolver.MaxLevel)
						ladder.Add(low);
					else
						break;
				}

				var complete = ladder.Count == byRank.Count;
				var repairable = complete
				                 && !ladder.Zip(ladder.Skip(1)).Any(p => p.Second < p.First)
				                 && ladder.Distinct().Count() > fieldLevels;

				collapsed.Add((family,
					string.Join("/", byRank.Select(r => r.First().Level)),
					complete
						? string.Join("/", ladder)
						: "-",
					repairable));
			}

			Console.WriteLine($"spell-level-ladder: {learnable.Count} learnable rank families, "
			                  + $"{learnable.Count - collapsed.Count} with one level per rank, {collapsed.Count} collapsing ranks onto a level.");

			if (collapsed.Count > 0)
			{
				Console.WriteLine("  family                        shipped ladder            effect-band ladder");
				foreach (var (family, field, band, repairable) in collapsed)
					Console.WriteLine($"  {(repairable ? "⚠️ " : "   ")}{family,-28}  {field,-24}  {band}");
			}

			var stale = collapsed.Where(c => c.Repairable).ToList();
			if (stale.Count == 0)
			{
				Console.WriteLine("  ✅ every remaining collapse has no second source - the effect bands answer partially, "
				                  + "step down with rank or run past the level cap. Those keep field 125's answer (Gitea #847).");
				return 0;
			}

			Console.WriteLine($"  ⚠️ {stale.Count} family/families the effect bands WOULD separate but the shipped rows do not - "
			                  + "faolan.db predates RdbSpellImporter.RankLadderFromEffectBands. Re-run:");
			Console.WriteLine("      dotnet run --project Faolan.Importer -- --only rdb-spells --Content:RdbDir <dir>");
			return 1;
		}

		// The "(Rank N)" suffix split off the family name; trailing spaces trimmed because a few catalog names carry a
		// double space before it ("Freeze  (Rank 2)"). The twin of `RdbSpellImporter.RankOf`, which cannot be shared -
		// this side reads DB rows and that side the records being imported.
		private static (string Family, int Rank)? LadderRankOf(string? name)
		{
			if (string.IsNullOrEmpty(name))
				return null;

			var at = name.IndexOf(LadderRankSuffix, StringComparison.Ordinal);
			if (at < 0 || !name.EndsWith(')'))
				return null;

			return int.TryParse(name.AsSpan((at + LadderRankSuffix.Length)..^1), out var rank)
				? (name[..at].TrimEnd(), rank)
				: null;
		}
	}
}
