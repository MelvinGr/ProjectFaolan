using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Faolan.Core.Progression;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report spell-level-ladder [--report-arg <family substring>]`: which spell FAMILIES ship a per-rank level
	// ladder, and which ship one level for several ranks - so that a single ding teaches two, three or six ranks of
	// the same spell at once.
	//
	// A family's ranks unlock at spaced levels (`Ice Strike` 7/20/35/50/65/80). `Spells.Level` comes from RDB field
	// 125, which states that ladder for most families and states no ladder for a handful: every rank of `Chill` reads
	// 7, every rank of `Infestation` reads 2, `Lifestrike`'s five ranks read 2/3/4/4/4. The symptom is in-game and
	// loud - a level-4 Necromancer dinging once was handed Lifestrike ranks 3, 4 and 5, the last of which states its
	// damage for levels 75-80.
	//
	// `RdbSpellImporter.RankLadderFromEffectBands` repairs the families where the record answers a second time (the
	// `InterpolatedHit` ramp's level band) completely and monotonically. It cannot repair the rest, and this is where
	// that residue is counted rather than left invisible.
	//
	// The residue is not one gap but two, and the TEACHING feat tells them apart. Every rank of a normal
	// family is granted by a feat - a `<Class> Class Feat NNN` for rank 1 and a `Spell Unlocking <lo>-<hi>` for ranks
	// 2 and up, whose name carries the level band it covers. Read against `FeatSpellGrants` the collapsed families
	// split cleanly:
	//
	//   * UNTAUGHT TOP rank - the family's highest rank is granted by no feat while every rank below it is, and it
	//     duplicates the level of the rank below. Nine families, and the predicate is exact: all nine top ranks sit in
	//     one late id block (>= 4487431) that nothing else in the catalog occupies, which is a second and completely
	//     independent characterisation of the same set. The captures agree where they can speak - the level-80
	//     Necromancer knows `Chill` ranks 1-5 and `Pestilential Blast` 1-6 but neither top rank, the level-80
	//     Demonologist `Hellfire Stream` 1-5 and `Shock` 1-4 but neither - four controlled misses, i.e. a miss with
	//     the rank below present as the control. These are not level-granted at all.
	//   * rank 1 from A class feat - rank 1 shares rank 2's level, and rank 1's only grant is a `<Class> Class
	//     Feat`, never a `Spell Unlocking` band. Field 125 copies the band-granted rank 2's level onto it, but the
	//     band is not the only thing the client states, so rank 1's own level is recoverable.
	//     A `<Class> Class Feat NNN` is a family of ten wire ids, and the client's featclass table (RDB 1000043)
	//     gives each one its own level. `RdbFeatImporter.DeriveClassSpellGrantLevels` writes it to
	//     `ClassSpell.GrantLevel`; this report prints it beside the extrapolation it replaced.
	//
	// The `Spell Unlocking` band is also a check on the level itself, and it is what independently confirmed the
	// effect-band repair: `Lifestrike` ranks 2/3/4/5 are granted by the 20-40, 40-60, 60-80 and 60-80 feats, which
	// fits the repaired 30/45/60/75 exactly. `Necromancer Class Feat 011` grants rank 1 of both `Chill` and
	// `Lifestrike`, so those two cannot unlock at different levels, and under the repaired bands they do not.
	//
	// Why a report and not a test: both sides are shipped data. The count of collapsed families
	// moves whenever the RDB import is re-run or the catalog grows, so pinning it would rebaseline on every data
	// change. What is asserted is the one direction that can only be a defect - a family the importer's rule would
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

			// The family is grouped over the whole catalog, not over the class spells, because that is what the
			// importer's rule groups over - and the difference decides cases. `Covenant of Pestilence` has three
			// records per rank (the cast plus its companion effects) whose bands disagree, so the rule declines it;
			// scoped to the one class-spell record per rank it would look repairable and this report would promise a
			// re-import that changes nothing. Only the display is narrowed, to the families a character can learn.
			var spells = await db.Spells.AsNoTracking()
				.Where(s => s.Level > 0)
				.Select(s => new
				{
					s.Id,
					s.Name,
					s.Level
				})
				.ToListAsync();

			// The level a record's numbers OPEN at, read exactly as `RdbSpellImporter.BandOpensAt` reads it: the
			// lowest band above 1, falling back to the lowest of all. A stepped ramp states `{1, 30, 35, 40, 45}` for
			// a rank rated 30, so the highest is its CEILING and only the lowest-above-floor is its own level - and
			// this column has to agree with the importer or it promises repairs that will not happen.
			var bands = (await db.SpellStatEffects.AsNoTracking()
					.Where(e => e.LowLevel != null)
					.Select(e => new
					{
						e.SpellId,
						Low = e.LowLevel!.Value
					})
					.ToListAsync())
				.GroupBy(e => e.SpellId)
				.ToDictionary(g => g.Key, g => (uint?)(g.Where(e => e.Low > 1).Select(e => e.Low).DefaultIfEmpty(g.Min(e => e.Low)).Min()));

			// (spell -> the names of the feats that teach it). The name is the payload here, not the id: it is what
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

				var grantLevels = await db.ClassSpells.AsNoTracking().Where(c => c.GrantLevel != null)
					.Select(c => new { c.SpellId, Level = c.GrantLevel!.Value })
					.ToListAsync();
				var bySpell = grantLevels.GroupBy(g => g.SpellId).ToDictionary(g => g.Key, g => g.Min(x => x.Level));
				RankOneCandidates(collapsed, learnable, bySpell);
				LadderMonotonicity(learnable, bySpell);
			}

			await ScheduleVersusStatedLevel(db);

			if (!string.IsNullOrWhiteSpace(familyFilter))
				Detail(learnable, familyFilter, bands, teachers, known, classSpells);

			var stale = collapsed.Where(c => c.Repairable).ToList();
			if (stale.Count == 0)
			{
				Console.WriteLine("  ✅ every remaining collapse has no second source in the effect bands - they answer partially, "
				                  + "step down with rank or run past the level cap. `ClassSpell.GrantLevel` answers them instead (Gitea #853).");
				return 0;
			}

			Console.WriteLine($"  ⚠️ {stale.Count} family/families the effect bands WOULD separate but the shipped rows do not - "
			                  + "faolan.db predates RdbSpellImporter.RankLadderFromEffectBands. Re-run:");
			Console.WriteLine("      dotnet run --project Faolan.Importer -- --only rdb-spells --Content:RdbDir <dir>");
			return 1;
		}

		/// <summary>
		///     Rank 1's real level, from the client's featclass schedule - printed beside what a ladder extrapolation
		///     would guess, so the two can be compared per family.
		/// </summary>
		/// <remarks>
		///     Rank 1's level is recoverable from the client, even though rank 1 is granted by a
		///     <c>&lt;Class&gt; Class Feat NNN</c> whose name carries no level band: the class feat is not one feat.
		///     It is a family of ten wire ids, and the client's featclass table (RDB 1000043,
		///     <c>FeatClassEntry</c>) gives each of them its own level - so the teaching feat states rank 1's level
		///     after all, just not in its name. <c>RdbFeatImporter.DeriveClassSpellGrantLevels</c> writes it to
		///     <see cref="Faolan.Core.Database.Tables.ClassSpell.GrantLevel" />, which is where the report reads it.
		///     <para />
		///     The extrapolation column stays, and stays REFUTED - it is the rival explanation, and printing it
		///     next to the answer is what shows the answer was needed. It was right on only ~60% of the families
		///     where the ladder was already known, and on `Spark of Life` it produces level 0, which is not a level a
		///     spell is granted at. The schedule gives that family 15.
		/// </remarks>
		private static void RankOneCandidates(
			List<(string Family, string Field, string Band, string Diagnosis, bool Repairable)> collapsed,
			IReadOnlyCollection<KeyValuePair<string, List<(uint Id, string Name, byte Level, int Rank)>>> learnable,
			IReadOnlyDictionary<uint, byte> grantLevels)
		{
			var affected = collapsed.Where(c => c.Diagnosis.Contains(RankOneFromAClassFeat, StringComparison.Ordinal)).ToList();
			if (affected.Count == 0)
				return;

			Console.WriteLine();
			Console.WriteLine("  ⭐ rank 1's level from the client's featclass SCHEDULE (ClassSpell.GrantLevel), against the ladder extrapolation it replaced:");
			Console.WriteLine($"     {"family",-28}  {"shipped",-22}  step  extrapolated  from the schedule");
			var byFamily = learnable.ToDictionary(f => f.Key, f => f.Value);
			foreach (var c in affected)
			{
				var levels = c.Field.Split('/').Select(v => int.TryParse(v, out var n) ? n : -1).ToList();
				if (levels.Count < 3 || levels.Any(l => l < 0))
					continue;

				// The step is taken from ranks 2->3, which are both real: rank 1 is the corrupted one and rank 2's
				// value is what it was copied from, so a step measured across 1->2 would be zero by construction.
				var step = levels[2] - levels[1];
				var candidate = levels[1] - step;
				// Over every rank-1 row, not the first. Several of these families ship the same rank twice on two
				// different `GuiStackingLine`s (two classes offering it), and only the row an auto-trained feat
				// actually teaches carries a grant level - so taking the first row reported "-" for three families
				// whose answer was sitting on the sibling id.
				var rankOne = "-";
				if (byFamily.TryGetValue(c.Family, out var ranks))
				{
					var lowest = ranks.Min(x => x.Rank);
					var found = ranks.Where(r => r.Rank == lowest && grantLevels.ContainsKey(r.Id)).Select(r => (int)grantLevels[r.Id]).ToList();
					if (found.Count > 0)
						rankOne = found.Min() == found.Max() ? found[0].ToString() : $"{found.Min()}..{found.Max()}";
				}
				Console.WriteLine($"     {c.Family,-28}  {c.Field,-22}  {step,4}  {(candidate > 0 ? candidate.ToString() : $"{candidate} ⛔"),12}  {rankOne}");
			}

			// The control, and the reason the block above is quotable at all: the families whose ladder a second
			// source already fixed should satisfy the same rule, and how many do is the measurement.
			var controls = learnable.Select(f => f.Value).Where(f => f.Select(r => r.Level).Distinct().Count() == f.Select(r => r.Rank).Distinct().Count()).ToList();
			var agree = 0;
			var tested = 0;
			foreach (var family in controls)
			{
				var byRank = family.GroupBy(r => r.Rank).OrderBy(g => g.Key).Select(g => (int)g.First().Level).ToList();
				if (byRank.Count < 3)
					continue;

				var step = byRank[2] - byRank[1];
				if (step <= 0)
					continue;

				tested++;
				if (byRank[0] == byRank[1] - step)
					agree++;
			}

			var rate = tested == 0 ? 0 : agree * 100.0 / tested;
			Console.WriteLine($"     control - of {tested} families whose ladder is already one level per rank, {agree} put rank 1 exactly one step below rank 2"
			                  + $" ({rate:F0}%).");

			// The verdict, and it is the control that gives it rather than the candidates above. An extrapolation
			// is only worth applying if it is right where the answer is already known, and this one is not: a rule
			// that is wrong on four families in ten would write a fabricated level into content that currently
			// carries a merely-duplicated one. The candidate column stays because a HUMAN reading one family at a
			// time can weigh it; a sweep cannot.
			Console.WriteLine(rate >= 95
				? "     ⭐ The control is strong enough that the extrapolation would have been worth taking seriously per family."
				: $"     ⛔ REFUTED as a rule: right on only {rate:F0}% of the families where the answer is known - which is why the"
				  + " schedule column, not this one, is what got written (#853).");
		}

		/// <summary>
		///     How many rank ladders are monotonic under each of the two level sources - the measurement that decides
		///     the featclass schedule over field 125.
		/// </summary>
		/// <remarks>
		///     A rank ladder that steps down is incoherent whichever source states it, so this is a property the two
		///     can be scored against without a capture. Measured at the time of writing: field 125 breaks exactly one
		///     family - <c>Quell the Ether</c>, 45/30/45/60/75, the contradiction the ticket filed as unrecoverable -
		///     and the schedule breaks none, reading 15/30/45/60/75 for it.
		///     <para />
		///     printed, not asserted. A ladder is the client's statement and a future build could legitimately
		///     ship an odd one; what would be a defect is our derivation inventing the disorder, and that shows up
		///     here as a count moving off zero rather than as a threshold to pin.
		/// </remarks>
		private static void LadderMonotonicity(
			IReadOnlyCollection<KeyValuePair<string, List<(uint Id, string Name, byte Level, int Rank)>>> learnable,
			IReadOnlyDictionary<uint, byte> grantLevels)
		{
			var scored = 0;
			var statedBroken = new List<string>();
			var derivedBroken = new List<string>();

			foreach (var (family, ranks) in learnable)
			{
				// One row per rank, and only families every rank of which the schedule answers - a partial ladder
				// would score the gap rather than the source.
				var byRank = ranks.GroupBy(r => r.Rank).OrderBy(g => g.Key).ToList();
				if (byRank.Count < 2)
					continue;

				// The same rows on both sides. Several families ship a rank twice (two `GuiStackingLine`s) and only
				// one of the pair is the row an auto-trained feat teaches - so scoring `Spells.Level` over min-of-all
				// rows while scoring the schedule over the taught row lets field 125 off: `Quell the Ether`'s taught
				// rank 1 reads 45, its sibling 30, and taking the min silently reported a clean ladder.
				var taught = byRank.Select(g => g.Where(r => grantLevels.ContainsKey(r.Id)).ToList()).ToList();
				if (taught.Any(g => g.Count == 0))
					continue;

				scored++;
				var derived = taught.Select(g => g.Min(r => (int)grantLevels[r.Id])).ToList();
				var stated = taught.Select(g => g.Max(r => (int)r.Level)).ToList();
				if (Descends(stated))
					statedBroken.Add($"{family} ({string.Join("/", stated)})");
				if (Descends(derived))
					derivedBroken.Add($"{family} ({string.Join("/", derived)})");
			}

			if (scored == 0)
				return;

			Console.WriteLine();
			Console.WriteLine($"  ⭐ ladder monotonicity over the {scored} families the featclass schedule answers in full:");
			Console.WriteLine($"     Spells.Level (RDB field 125)   : {statedBroken.Count} non-monotonic" + (statedBroken.Count > 0 ? "  - " + string.Join(", ", statedBroken) : ""));
			Console.WriteLine($"     ClassSpell.GrantLevel (1000043): {derivedBroken.Count} non-monotonic" + (derivedBroken.Count > 0 ? "  - " + string.Join(", ", derivedBroken) : ""));

			return;

			static bool Descends(List<int> ladder)
			{
				for (var i = 1; i < ladder.Count; i++)
				{
					if (ladder[i] < ladder[i - 1])
						return true;
				}

				return false;
			}
		}

		/// <summary>
		///     The two level sources side by side, per class: <c>ClassSpell.GrantLevel</c> (the client's featclass
		///     schedule) against <c>Spells.Level</c> (RDB field 125), over every row the schedule answers.
		/// </summary>
		/// <remarks>
		///     The point of the table is the disagreements. The two fields are independent - the schedule says when a
		///     class is handed the spell, field 125 is the record's own level word - so a difference is not
		///     automatically a defect. It does rule out "retail never grants above field 125": the rows that differ
		///     upward refute that outright, and they are
		///     printed by name rather than counted, because a one-directional rule read off three rows is exactly the
		///     thing a full sweep exists to kill.
		///     <para />
		///     printed, not asserted: both populations are shipped data and both move whenever the RDB
		///     import is re-run. The gate that can only be a defect is <c>--report spell-grant-check</c>, which scores
		///     the gate this column feeds against the captured spellbooks.
		/// </remarks>
		private static async Task ScheduleVersusStatedLevel(DatabaseContext db)
		{
			var rows = await db.ClassSpells.AsNoTracking()
				.Where(cs => cs.GrantLevel != null)
				.Join(db.Spells.AsNoTracking(), cs => cs.SpellId, s => s.Id, (cs, s) => new
				{
					cs.Class,
					s.Name,
					Schedule = (int)cs.GrantLevel!.Value,
					Stated = (int)s.Level
				})
				.ToListAsync();

			if (rows.Count == 0)
			{
				Console.WriteLine();
				Console.WriteLine("  (no ClassSpell.GrantLevel rows - run `--only rdb-feats` to derive the schedule)");
				return;
			}

			Console.WriteLine();
			Console.WriteLine($"  ⭐ the featclass SCHEDULE against Spells.Level, per class ({rows.Count} rows the schedule answers):");
			Console.WriteLine($"     {"class",-18} {"rows",5} {"agree",6} {"differ",7} {"below",6} {"above",6}");
			foreach (var group in rows.GroupBy(r => r.Class).OrderBy(g => g.Key.ToString(), StringComparer.Ordinal))
			{
				var differ = group.Where(r => r.Schedule != r.Stated).ToList();
				Console.WriteLine($"     {group.Key.ToString(),-18} {group.Count(),5} {group.Count() - differ.Count,6} {differ.Count,7}"
				                  + $" {differ.Count(r => r.Schedule < r.Stated),6} {differ.Count(r => r.Schedule > r.Stated),6}");
			}

			var all = rows.Where(r => r.Schedule != r.Stated).ToList();
			Console.WriteLine($"     {"TOTAL",-18} {rows.Count,5} {rows.Count - all.Count,6} {all.Count,7}"
			                  + $" {all.Count(r => r.Schedule < r.Stated),6} {all.Count(r => r.Schedule > r.Stated),6}");

			// The classes the schedule says nothing about, printed because their absence is the finding: the melee
			// kit is mostly COMBOS, and no feat teaches a combo - so the schedule can only ever corroborate or correct
			// field 125 on the caster classes, never replace it wholesale.
			var silent = Enum.GetValues<ConanClass>().Where(c => c != default && rows.All(r => r.Class != c)).ToList();
			if (silent.Count > 0)
				Console.WriteLine($"     ⚠️ the schedule answers NOTHING for {string.Join(", ", silent)} - no feat teaches a combo, so these classes stay on Spells.Level.");

			// The refuting half, named. These are the rows where the class is handed the spell above the level its
			// own record states - i.e. field 125 is not a ceiling.
			var above = all.Where(r => r.Schedule > r.Stated).OrderByDescending(r => r.Schedule - r.Stated).ToList();
			if (above.Count == 0)
				return;

			Console.WriteLine($"     ⛔ granted ABOVE the stated level on {above.Count} row(s) - field 125 is not a ceiling:");
			foreach (var row in above)
				Console.WriteLine($"        {row.Class,-18} {row.Name,-34} schedule {row.Schedule,3}  stated {row.Stated,3}");
		}

		private const string UntaughtTopRank = "untaught top rank (not level-granted)";
		private const string RankOneFromAClassFeat = "rank 1 from a class feat, level copied from rank 2";

		// What the feat graph says about one collapsed family. Both shapes are structural rather than per-value, which
		// is why they can be named without a per-family judgement call.
		private static string Diagnose(List<IGrouping<int, (uint Id, string Name, byte Level, int Rank)>> byRank,
			Dictionary<uint, string[]> teachers)
		{
			var findings = new List<string>();
			var taught = byRank.Select(r => r.Any(s => teachers.ContainsKey(s.Id))).ToList();

			// The top rank is granted by nothing while every rank below it is, and it repeats the level below. The
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

			// A rank whose level falls outside the band its own teaching feat names. Not a repair - it says the two
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

			// Only a rank with exactly one band feat can be judged: several bands on one spell is the client listing a
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
