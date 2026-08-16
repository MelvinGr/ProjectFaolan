using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core.Progression;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report spell-level-ladder [--report-arg <family substring>]`: which spell FAMILIES ship a per-rank level
	// ladder, and which ship one level for several ranks - so that a single ding teaches two, three or six ranks of
	// the same spell at once.
	//
	// A family's ranks unlock at spaced levels (`Ice Strike` 7/20/35/50/65/80). `Spells.Level` comes from RDB field
	// 125, which states that ladder for most families and states NO ladder for a handful: every rank of `Chill` reads
	// 7, every rank of `Infestation` reads 2, `Lifestrike`'s five ranks read 2/3/4/4/4. The symptom is in-game and
	// loud - a level-4 Necromancer dinging once was handed Lifestrike ranks 3, 4 AND 5, the last of which states its
	// damage for levels 75-80.
	//
	// `RdbSpellImporter.RankLadderFromEffectBands` repairs the families where the record answers a second time (the
	// `InterpolatedHit` ramp's level band) completely and monotonically. It cannot repair the rest, and this is where
	// that residue is counted rather than left invisible.
	//
	// ⭐ **The residue is not one gap but two, and the TEACHING FEAT tells them apart.** Every rank of a normal
	// family is granted by a feat - a `<Class> Class Feat NNN` for rank 1 and a `Spell Unlocking <lo>-<hi>` for ranks
	// 2 and up, whose NAME carries the level band it covers. Read against `FeatSpellGrants` the collapsed families
	// split cleanly:
	//
	//   * **UNTAUGHT TOP RANK** - the family's highest rank is granted by NO feat while every rank below it is, and it
	//     duplicates the level of the rank below. Nine families, and the predicate is exact: all nine top ranks sit in
	//     one late id block (>= 4487431) that nothing else in the catalog occupies, which is a second and completely
	//     independent characterisation of the same set. The captures agree where they can speak - the level-80
	//     Necromancer knows `Chill` ranks 1-5 and `Pestilential Blast` 1-6 but neither top rank, the level-80
	//     Demonologist `Hellfire Stream` 1-5 and `Shock` 1-4 but neither - four controlled misses, i.e. a miss with
	//     the rank BELOW present as the control. These are not level-granted at all.
	//   * **RANK 1 FROM A CLASS FEAT** - rank 1 shares rank 2's level, and rank 1's only grant is a `<Class> Class
	//     Feat`, never a `Spell Unlocking` band. Field 125 copied the band-granted rank 2's level onto it. The real
	//     level is NOT recoverable: the feat record carries no level field (five teaching feats diffed - the fixed
	//     header is byte-identical up to the name), so the band is all the client states and rank 1 has none.
	//
	// The `Spell Unlocking` band is also a CHECK on the level itself, and it is what independently confirmed the
	// effect-band repair: `Lifestrike` ranks 2/3/4/5 are granted by the 20-40, 40-60, 60-80 and 60-80 feats, which
	// fits the repaired 30/45/60/75 exactly and fits the old 3/4/4/4 not at all. `Necromancer Class Feat 011` grants
	// rank 1 of BOTH `Chill` and `Lifestrike`, so those two cannot unlock at different levels - and after the repair
	// they do not (15 and 15, against 7 and 2 before).
	//
	// ⭐ **Why a report and not a test** (Gitea #729): both sides are shipped DATA. The count of collapsed families
	// moves whenever the RDB import is re-run or the catalog grows, so pinning it would rebaseline on every data
	// change. What is asserted is the one direction that can only be a defect - a family the importer's rule WOULD
	// separate but whose shipped rows are still collapsed, i.e. `faolan.db` predates the rule and the module needs
	// re-running.
	public static partial class ReportCli
	{
		private const string LadderRankSuffix = " (Rank ";

		// `Spell Unlocking 20-40`, `Spell Unlocking 60-80 3` - the band is the name, and the trailing ordinal (where
		// there is one) is a copy index, not part of it.
		private static readonly Regex SpellUnlockingBand = new Regex(@"^Spell Unlocking (\d+)-(\d+)", RegexOptions.Compiled);

		private static async Task<int> SpellLevelLadder(string? familyFilter)
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

			// (spell -> the names of the feats that teach it). The NAME is the payload here, not the id: it is what
			// carries the level band and what separates a class feat from a band feat.
			var teachers = (await db.FeatSpellGrants.AsNoTracking()
					.Join(db.Feats.AsNoTracking(), g => g.FeatId, f => f.Id, (g, f) => new
					{
						g.SpellId,
						f.Name
					})
					.ToListAsync())
				.GroupBy(x => x.SpellId)
				.ToDictionary(g => g.Key, g => g.Select(x => x.Name).Distinct().ToArray());

			// Every captured character's known-spell list, as the oracle a diagnosis can be controlled against.
			var known = (await db.CharacterSpells.AsNoTracking().Select(cs => cs.SpellId).ToListAsync()).ToHashSet();

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
			var collapsed = new List<(string Family, string Field, string Band, string Diagnosis, bool Repairable)>();
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
					Diagnose(byRank, teachers),
					repairable));
			}

			Console.WriteLine($"spell-level-ladder: {learnable.Count} learnable rank families, "
			                  + $"{learnable.Count - collapsed.Count} with one level per rank, {collapsed.Count} collapsing ranks onto a level.");

			if (collapsed.Count > 0)
			{
				Console.WriteLine($"  {"family",-28}  {"shipped",-22}  {"effect bands",-22}  what the teaching feats say");
				foreach (var (family, field, band, diagnosis, repairable) in collapsed)
					Console.WriteLine($"  {(repairable ? "⚠️ " : "   ")}{family,-28}  {field,-22}  {band,-22}  {diagnosis}");

				foreach (var kind in new[] { UntaughtTopRank, RankOneFromAClassFeat })
					Console.WriteLine($"  {collapsed.Count(c => c.Diagnosis.Contains(kind, StringComparison.Ordinal)),3} x {kind}");
			}

			if (!string.IsNullOrWhiteSpace(familyFilter))
				Detail(learnable, familyFilter, bands, teachers, known, classSpells);

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

		private const string UntaughtTopRank = "untaught top rank (not level-granted)";
		private const string RankOneFromAClassFeat = "rank 1 from a class feat, level copied from rank 2";

		// What the feat graph says about ONE collapsed family. Both shapes are structural rather than per-value, which
		// is why they can be named without a per-family judgement call.
		private static string Diagnose(List<IGrouping<int, (uint Id, string Name, byte Level, int Rank)>> byRank,
			Dictionary<uint, string[]> teachers)
		{
			var findings = new List<string>();
			var taught = byRank.Select(r => r.Any(s => teachers.ContainsKey(s.Id))).ToList();

			// The top rank is granted by nothing while every rank below it is, AND it repeats the level below. The
			// conjunction is what makes it exact - "untaught" alone also catches the Bear Shaman totems, whose UNtaught
			// ranks are the low ones and whose top rank is taught.
			if (byRank.Count >= 2 && !taught[^1] && taught.Take(byRank.Count - 1).All(t => t)
			    && byRank[^1].First().Level == byRank[^2].First().Level)
				findings.Add(UntaughtTopRank);

			// Rank 1 shares rank 2's level and is granted only by a class feat - which states no band, so field 125 had
			// nothing of rank 1's own to copy and took its neighbour's.
			if (byRank.Count >= 2 && byRank[0].Key == 1 && byRank[0].First().Level == byRank[1].First().Level)
			{
				var rank1Feats = byRank[0].SelectMany(s => teachers.GetValueOrDefault(s.Id) ?? []).ToArray();
				if (rank1Feats.Length > 0 && rank1Feats.All(n => !SpellUnlockingBand.IsMatch(n)))
					findings.Add(RankOneFromAClassFeat);
			}

			// A rank whose level falls OUTSIDE the band its own teaching feat names. Not a repair - it says the two
			// client sources disagree about this family, which is a different (and louder) problem than a copy.
			var outside = byRank.SelectMany(r => r).Count(s => OutsideItsBand(s.Level, teachers.GetValueOrDefault(s.Id)));
			if (outside > 0)
				findings.Add($"{outside} rank(s) outside the band their own feat names");

			if (findings.Count == 0)
				findings.Add(byRank.SelectMany(r => r).Any(s => teachers.ContainsKey(s.Id))
					? "no shape identified"
					: "no teaching feat on any rank");

			return string.Join("; ", findings);
		}

		private static bool OutsideItsBand(byte level, string[]? featNames)
		{
			if (featNames == null)
				return false;

			var bands = featNames.Select(n => SpellUnlockingBand.Match(n)).Where(m => m.Success).ToList();

			// Only a rank with exactly ONE band feat can be judged: several bands on one spell is the client listing a
			// spell under more than one unlock feat, and then no single band is its statement.
			return bands.Count == 1
			       && (level < uint.Parse(bands[0].Groups[1].Value) || level > uint.Parse(bands[0].Groups[2].Value));
		}

		// `--report-arg <substring>`: every rank of the matching families, with its level, its effect band, whether a
		// captured character knows it, and the feats that teach it. This is the view the two shapes were read off.
		private static void Detail(List<KeyValuePair<string, List<(uint Id, string Name, byte Level, int Rank)>>> learnable,
			string filter, Dictionary<uint, uint?> bands, Dictionary<uint, string[]> teachers, HashSet<uint> known, HashSet<uint> classSpells)
		{
			var matches = learnable.Where(f => f.Key.Contains(filter, StringComparison.OrdinalIgnoreCase))
				.OrderBy(f => f.Key, StringComparer.Ordinal).ToList();

			Console.WriteLine();
			Console.WriteLine($"detail for '{filter}' ({matches.Count} family/families):");
			foreach (var (family, list) in matches)
			{
				Console.WriteLine($"  {family}");
				foreach (var spell in list.OrderBy(s => s.Rank).ThenBy(s => s.Id))
					Console.WriteLine($"    rank {spell.Rank}  id {spell.Id,-9} level {spell.Level,3}  band {bands.GetValueOrDefault(spell.Id)?.ToString() ?? "-",-4}"
					                  + $"  {(known.Contains(spell.Id) ? "KNOWN" : "     ")}  {(classSpells.Contains(spell.Id) ? "class" : "     ")}"
					                  + $"  {string.Join(", ", teachers.GetValueOrDefault(spell.Id) ?? ["(no teaching feat)"])}");
			}
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
