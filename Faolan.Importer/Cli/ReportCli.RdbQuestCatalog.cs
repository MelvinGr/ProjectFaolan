using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Protobuf;
using Faolan.Core.Rdb;

namespace Faolan.Importer.Cli
{
	// `--report rdb-quest-catalog` - the client's own quest catalog, RDB type 1000104.
	//
	// This is the `RDBQUESTGIVERS` data table the client prints at startup: a 12-byte RDB header followed by a raw
	// `QuestGiverList` protobuf, whose schema this repo already carried in `Faolan.Core/Protobuf` but had never read.
	// It is the DEFINITION side of the quest system - the wire only ever shows the quests a captured character
	// happened to hold, while this ships every quest in the game, with the designers' names in plaintext (no LDB
	// page, no captured decryption key).
	//
	// The client's model is MainQuest (a quest line) -> Quest (a step) -> QuestGoal, and it maps onto our tables
	// one-for-one: `QuestLine.QuestLineId` is `MainQuest.template_id`, `QuestDefinition.QuestId` is `Quest.template_id`,
	// and `QuestLine.Magic` is `MainQuest.hash` (the ACGHash line token). That last one is what VERIFIES this
	// report rather than merely asserting it - see the `hash` cross-check the summary prints.
	//
	// Several right-shaped fields in the schema are dead in the shipped data, and the census below prints the
	// populated count for every one of them precisely so nobody plans a decode around a column of zeroes. Known
	// empties: `MainQuest.type`, `Quest.tier`, `Quest.start_new_tier`, and both quest-giver lists.
	public static partial class ReportCli
	{
		// RDB type of the RDBQUESTGIVERS data table, and the fixed header the payload sits behind. The header is
		// why a protobuf parse at offset 0 fails - the first sweep for this table reported no hits for that reason.
		private const uint QuestCatalogRdbType = 1000104;
		private const int QuestCatalogHeaderBytes = 12;

		// The four line tokens `docs/re/questc-region-decode.md` recovered from captures, kept here as an
		// independent check on the table: they were read off the wire, the hashes below come out of the client's
		// data file, and nothing connects the two but the claim that our QuestLineId is the client's MainQuest id.
		private static readonly (uint LineId, string Token)[] CapturedLineTokens =
			[(241, "W6QT"), (1054, "RRL3"), (1772, "B0DN"), (1834, "3734")];

		private static async Task RdbQuestCatalog(string rdbDir, string? arg)
		{
			var wanted = ParseIdFilter(arg);
			var catalog = ReadQuestCatalog(rdbDir, ReportRdbProxy());
			if (catalog == null)
			{
				Console.WriteLine($"rdb-quest-catalog: RDB type {QuestCatalogRdbType} not found - is RdbDir pointing at the client's RDB folder?");
				return;
			}

			var chapters = catalog.MainQuests.GroupBy(m => m.TemplateId).ToDictionary(g => g.Key, g => g.First());
			var quests = catalog.Quests.GroupBy(q => q.TemplateId).ToDictionary(g => g.Key, g => g.First());
			var goalsByQuest = catalog.QuestGoals.ToLookup(g => g.QuestTemplateId);
			var questsByChapter = catalog.Quests.ToLookup(q => q.MainQuestTemplateId);

			Console.WriteLine($"=== RDB {QuestCatalogRdbType} (RDBQUESTGIVERS), version {catalog.Version} ===");
			Console.WriteLine($"lines    (MainQuest) {catalog.MainQuests.Count} rows / {chapters.Count} distinct ids");
			Console.WriteLine($"quests   (Quest)     {catalog.Quests.Count} rows / {quests.Count} distinct ids");
			Console.WriteLine($"goals    (QuestGoal) {catalog.QuestGoals.Count} rows");
			Console.WriteLine($"quest givers {catalog.QuestGivers.Count}, giver positions {catalog.quest_giver_pos.Count}");

			// A repeated id is not noise to be deduped away silently: 2 chapters and 15 goals have duplicate ids whose
			// rows DIFFER, so "first record wins" would drop real data (the same trap as the repeated wire records).
			// Count them here, and the tree below prints every distinct variant rather than picking one.
			Console.WriteLine();
			Console.WriteLine("--- duplicate template ids (a repeated id whose rows DIFFER carries two definitions) ---");
			DuplicateCensus("MainQuest", catalog.MainQuests.Select(m => (m.TemplateId, Row: ChapterRow(m))));
			DuplicateCensus("Quest", catalog.Quests.Select(q => (q.TemplateId, Row: QuestRow(q))));
			DuplicateCensus("QuestGoal", catalog.QuestGoals.Select(g => (g.TemplateId, Row: GoalRow(g))));

			// The verification, printed rather than assumed. A mismatch means the QuestLineId <-> MainQuest identity is
			// wrong and every other line of this report is suspect.
			Console.WriteLine();
			Console.WriteLine("--- cross-check: MainQuest.hash vs the line tokens recovered from captures ---");
			var agree = 0;
			foreach (var (lineId, token) in CapturedLineTokens)
			{
				var got = chapters.TryGetValue(lineId, out var m)
					? m.Hash
					: "(no such chapter)";
				var ok = got == token;
				agree += ok ? 1 : 0;
				Console.WriteLine($"  line {lineId,-6} captured '{token}'  catalog '{got}'  {(ok ? "OK" : "MISMATCH")}");
			}

			Console.WriteLine($"  {agree}/{CapturedLineTokens.Length} agree" +
			                  (agree == CapturedLineTokens.Length ? "" : "  ⚠️  the QuestLineId <-> MainQuest.template_id identity does NOT hold"));

			// The census is the point of this block: a field that is populated on 0 rows is not a decode target, and
			// several of these look decisive until you count them.
			Console.WriteLine();
			Console.WriteLine("--- populated-field census (rows where the field is non-default) ---");
			Census("MainQuest.first_quest_template_id", catalog.MainQuests.Count(m => m.FirstQuestTemplateId != 0), chapters.Count);
			Census("MainQuest.last_quest_template_id", catalog.MainQuests.Count(m => m.LastQuestTemplateId != 0), chapters.Count);
			Census("MainQuest.prev_template_id", catalog.MainQuests.Count(m => m.PrevTemplateId != 0), chapters.Count);
			Census("MainQuest.next_template_id", catalog.MainQuests.Count(m => m.NextTemplateId != 0), chapters.Count);
			Census("MainQuest.condition_script_id", catalog.MainQuests.Count(m => m.ConditionScriptId != 0), chapters.Count);
			Census("MainQuest.first_main_quest_template_id", catalog.MainQuests.Count(m => m.FirstMainQuestTemplateId != 0), chapters.Count);
			Census("MainQuest.last_main_quest_template_id", catalog.MainQuests.Count(m => m.LastMainQuestTemplateId != 0), chapters.Count);
			Census("MainQuest.type", catalog.MainQuests.Count(m => m.Type != 0), chapters.Count);
			Census("MainQuest.tier", catalog.MainQuests.Count(m => m.Tier != 0), chapters.Count);
			Census("MainQuest.xp_reward", catalog.MainQuests.Count(m => m.XpReward != 0), chapters.Count);
			Census("MainQuest.cash_reward", catalog.MainQuests.Count(m => m.CashReward != 0), chapters.Count);
			Census("MainQuest.reward", catalog.MainQuests.Count(m => m.Rewards.Count > 0), chapters.Count);
			Census("MainQuest.optional_reward", catalog.MainQuests.Count(m => m.OptionalRewards.Count > 0), chapters.Count);
			Census("Quest.main_quest_template_id", catalog.Quests.Count(q => q.MainQuestTemplateId != 0), quests.Count);
			Census("Quest.start_scry_script_id", catalog.Quests.Count(q => q.StartScryScriptId != 0), quests.Count);
			Census("Quest.tier", catalog.Quests.Count(q => q.Tier != 0), quests.Count);
			Census("Quest.start_new_tier", catalog.Quests.Count(q => q.StartNewTier), quests.Count);
			Census("QuestGoal.phase", catalog.QuestGoals.Count(g => g.Phase != 0), catalog.QuestGoals.Count);

			// The census above counts non-default values, which cannot tell a field retail never emits from one it
			// emits as zero - a generated getter reads both back as 0. The raw walk below can, and it also answers the
			// question the schema cannot: is there anything in this record we do not parse?
			RawFieldCensus(rdbDir);

			// `prev` is a single-parent PREREQUISITE pointer between chapters, not a doubly-linked order: it is
			// populated two orders of magnitude more often than `next`, and almost none of the links are reciprocal.
			// Saying so with the numbers beside it is what stops the next reader reading it as a quest chain.
			var prevLinks = catalog.MainQuests.Where(m => m.PrevTemplateId != 0).ToList();
			var reciprocal = prevLinks.Count(m => chapters.TryGetValue(m.PrevTemplateId, out var p) && p.NextTemplateId == m.TemplateId);
			var dangling = prevLinks.Where(m => !chapters.ContainsKey(m.PrevTemplateId)).Select(m => m.PrevTemplateId).Distinct().ToList();
			Console.WriteLine();
			Console.WriteLine("--- quest-line ordering ---");
			Console.WriteLine($"  prev links {prevLinks.Count}, of which {reciprocal} are mirrored by a next link -> prerequisite pointer, NOT a linked list");
			Console.WriteLine($"  prev targets that are not a line in this table: {(dangling.Count == 0 ? "none" : string.Join(", ", dangling))}");
			Console.WriteLine($"  lines holding more than one quest: {questsByChapter.Count(g => g.Count() > 1)}");
			Console.WriteLine("  ⚠️  no FIELD orders the steps within a chapter - Quest.tier and Quest.start_new_tier, the");
			Console.WriteLine("      fields shaped for exactly that, are empty (census above). The ROW ORDER is scored below.");

			ChapterGroupCheck(catalog, chapters);

			await StepOrderCheck(catalog);
			await LineRewardCheck(catalog);

			if (wanted.Count > 0)
			{
				// Filtered: print the tree around whatever the caller named, accepting either a chapter id or a
				// quest id, because a reader chasing one quest rarely knows which of the two they are holding.
				var picked = chapters.Values
					.Where(m => wanted.Contains(m.TemplateId) || questsByChapter[m.TemplateId].Any(q => wanted.Contains(q.TemplateId)))
					.OrderBy(m => m.TemplateId);

				foreach (var m in picked)
				{
					Console.WriteLine();
					Console.WriteLine($"line {m.TemplateId}  '{m.Name}'  hash={m.Hash}");
					Console.WriteLine($"  first={m.FirstQuestTemplateId} last={m.LastQuestTemplateId} prev={m.PrevTemplateId} next={m.NextTemplateId} " +
					                  $"condScript={m.ConditionScriptId} xp={m.XpReward} cash={m.CashReward}");
					if (m.Rewards.Count > 0)
						Console.WriteLine($"  rewards {string.Join(", ", m.Rewards.Select(r => $"{r.TemplateId}:{r.Type}"))}");
					if (m.OptionalRewards.Count > 0)
						Console.WriteLine($"  optional rewards {string.Join(", ", m.OptionalRewards.Select(r => $"{r.TemplateId}:{r.Type}"))}");

					// Distinct rows, not distinct ids: an id repeated with identical content is one definition listed
					// twice and printing it twice is just noise, while an id repeated with DIFFERING content is two
					// definitions and dropping either would be a silent loss.
					foreach (var q in questsByChapter[m.TemplateId].DistinctBy(QuestRow).OrderBy(q => q.TemplateId))
					{
						Console.WriteLine($"    quest {q.TemplateId}  '{q.Name}'  hash={q.Hash} scry={q.StartScryScriptId}");
						foreach (var g in goalsByQuest[q.TemplateId].DistinctBy(GoalRow).OrderBy(g => g.TemplateId))
							Console.WriteLine($"      goal {g.TemplateId}  '{g.Name}'  hash={g.Hash} phase={g.Phase}");
					}
				}

				return;
			}

			// Unfiltered: three flat TSV sections, which is what a diff or a spreadsheet wants. The tree form above
			// is for reading one chapter; this is for reading the catalog.
			Console.WriteLine();
			Console.WriteLine("=== lines    (MainQuest) ===");
			Console.WriteLine("templateId\thash\ttier\tfirstQuest\tlastQuest\tprev\tnext\tcondScript\txpReward\tcashReward\trewards\toptionalRewards\tname");
			foreach (var row in catalog.MainQuests.OrderBy(m => m.TemplateId).Select(ChapterRow))
				Console.WriteLine(row);

			Console.WriteLine();
			Console.WriteLine("=== quests (Quest) ===");
			Console.WriteLine("templateId\thash\tchapter\tstartScryScript\ttier\tstartNewTier\tname");
			foreach (var row in catalog.Quests.OrderBy(q => q.TemplateId).Select(QuestRow))
				Console.WriteLine(row);

			Console.WriteLine();
			Console.WriteLine("=== goals (QuestGoal) ===");
			Console.WriteLine("templateId\thash\tquest\tphase\tname");
			foreach (var row in catalog.QuestGoals.OrderBy(g => g.TemplateId).Select(GoalRow))
				Console.WriteLine(row);

			return;

			static void Census(string field, int populated, int total)
			{
				var note = populated == 0
					? "   <-- EMPTY in the shipped data"
					: "";
				Console.WriteLine($"  {field,-38} {populated,6} / {total,-6}{note}");
			}

			static void DuplicateCensus(string list, IEnumerable<(uint Id, string Row)> rows)
			{
				var byId = rows.GroupBy(r => r.Id).Where(g => g.Count() > 1).ToList();
				var differing = byId.Where(g => g.Select(r => r.Row).Distinct().Count() > 1).Select(g => g.Key).ToList();
				var note = differing.Count == 0
					? ""
					: $"  ⚠️  {differing.Count} of them DIFFER: {string.Join(", ", differing.OrderBy(i => i))}";
				Console.WriteLine($"  {list,-10} {byId.Count,4} repeated id(s){note}");
			}
		}

		// Does the catalog's own row order give the step order within a quest line?
		//
		// is `Quest_c` @+0xf0/@+0xf8 the line's reward? The catalog is the only place that can answer it - it
		// gives a quest line exactly one `xp_reward`/`cash_reward` pair, keyed by `MainQuest.template_id`, which is our
		// `QuestLineId` (the hash cross-check above is what establishes that identity).
		//
		// Three things are printed, because the question has three parts and only the middle one had been asked:
		//
		//   1. does the carrier's @+0xf0 equal its line's catalog xp_reward, and @+0xf8 the cash_reward;
		//   2. is a line's value repeated on its other captured steps (the constancy the line reading predicts);
		//   3. where in the line the carrier sits, against the captured `JournalSequence`.
		//
		// Part 2 needs a sibling that was actually captured. A step with `HasSnapshotRecord = 0` carries 0 because
		// no snapshot ever held it, which says nothing - counting those as disagreement would falsify the reading with
		// missing data.
		// `MainQuest.first_main_quest_template_id` / `last_main_quest_template_id` (fields 11 and 12) - populated on
		// 1791 of 2181 chapters and, until this block, read by nothing: not the census above, not the importer, not
		// the model. They went unnoticed because the census listed every other field, which is exactly the shape a
		// missing row hides in.
		//
		// They look like the chapter-level twin of `first_quest_template_id`/`last_quest_template_id`: those bound a
		// chapter's STEPS, so these should bound a group of CHAPTERS.
		//
		// measured, and the reading does not survive: the pair is DEGENERATE. Both ends resolve to a real chapter
		// on all 1791 rows, but 1788 have `first` == the row's own id and 1786 have `first` == `last`, i.e. a group of
		// exactly one. Only three genuine multi-chapter groups exist in the whole table - {2495, 2496} -> 2549,
		// {1741, 2908}, and 3279 (which is one of the two duplicated ids). So the field carries a self-echo on 99.7%
		// of rows and there is nothing here worth a column; `tier` does separate members where a group is real, but
		// three groups is not a feature. Kept as a report line rather than an import so the next reader sees the
		// measurement instead of re-deriving it.
		private static void ChapterGroupCheck(QuestGiverList catalog, Dictionary<uint, QuestGiverList.MainQuest> chapters)
		{
			var withGroup = catalog.MainQuests.Where(m => m.FirstMainQuestTemplateId != 0 || m.LastMainQuestTemplateId != 0).ToList();
			Console.WriteLine();
			Console.WriteLine("--- chapter GROUP pointers (first/last_main_quest_template_id, fields 11/12) ---");
			if (withGroup.Count == 0)
			{
				Console.WriteLine("  empty in the shipped data");
				return;
			}

			var resolvableFirst = withGroup.Count(m => chapters.ContainsKey(m.FirstMainQuestTemplateId));
			var resolvableLast = withGroup.Count(m => chapters.ContainsKey(m.LastMainQuestTemplateId));
			var selfFirst = withGroup.Count(m => m.FirstMainQuestTemplateId == m.TemplateId);
			var same = withGroup.Count(m => m.FirstMainQuestTemplateId == m.LastMainQuestTemplateId);
			Console.WriteLine($"  rows carrying the pair {withGroup.Count} / {chapters.Count}");
			Console.WriteLine($"  first resolves to a chapter in this table: {resolvableFirst}/{withGroup.Count}   last: {resolvableLast}/{withGroup.Count}");
			Console.WriteLine($"  rows whose first == their own id: {selfFirst}   rows where first == last (group of one): {same}");

			// If this really is a grouping, the members sharing a (first,last) pair form a set, and `tier` should be
			// the member's position in it. Printing the group SIZES says whether the pair partitions the table or is
			// just a per-row echo.
			var groups = withGroup.GroupBy(m => (m.FirstMainQuestTemplateId, m.LastMainQuestTemplateId)).ToList();
			Console.WriteLine($"  distinct (first,last) pairs: {groups.Count}  -> mean members {(double)withGroup.Count / groups.Count:F1}, largest {groups.Max(g => g.Count())}");
			var tierIsPosition = groups.Count(g => g.Select(m => m.Tier).Distinct().Count() == g.Count());
			Console.WriteLine($"  groups whose members carry DISTINCT tiers (tier = position in the group?): {tierIsPosition}/{groups.Count}");

			foreach (var g in groups.OrderByDescending(g => g.Count()).Take(3))
			{
				var members = g.OrderBy(m => m.Tier).Select(m => $"{m.TemplateId}(t{m.Tier})");
				Console.WriteLine($"    group first={g.Key.Item1} last={g.Key.Item2}: {string.Join(" ", members)}");
			}
		}

		private static async Task LineRewardCheck(QuestGiverList catalog)
		{
			await using var db = OpenDb();
			var quests = db.QuestDefinitions
				.Select(q => new { q.QuestId, q.QuestLineId, q.JournalSequence, q.FieldF0, q.FieldF8, q.XpReward, q.MoneyReward, q.HasSnapshotRecord })
				.AsEnumerable()
				.ToList();

			var byLine = quests.GroupBy(q => q.QuestLineId).ToDictionary(g => g.Key, g => g.ToList());
			var lines = catalog.MainQuests.GroupBy(m => m.TemplateId).ToDictionary(g => g.Key, g => g.First());

			int carriers = 0, xpAgree = 0, cashAgree = 0, noChapter = 0, chapterSilent = 0;
			int repeated = 0, contradicted = 0, first = 0, last = 0, middle = 0, unordered = 0;
			int atMin = 0, atMax = 0, multiStep = 0;
			var rows = new List<string>();

			foreach (var carrier in quests.Where(q => q.FieldF0 != 0).OrderBy(q => q.QuestLineId))
			{
				carriers++;
				if (!lines.TryGetValue(carrier.QuestLineId, out var chapter))
				{
					noChapter++;
					continue;
				}

				if (chapter.XpReward == 0 && chapter.CashReward == 0)
					chapterSilent++;

				var xpOk = chapter.XpReward == carrier.FieldF0;
				var cashOk = chapter.CashReward == carrier.FieldF8;
				xpAgree += xpOk ? 1 : 0;
				cashAgree += cashOk ? 1 : 0;

				// Constancy, over the siblings a snapshot actually captured.
				var siblings = byLine[carrier.QuestLineId].Where(q => q.QuestId != carrier.QuestId && q.HasSnapshotRecord).ToList();
				repeated += siblings.Count(q => q.FieldF0 == carrier.FieldF0);
				contradicted += siblings.Count(q => q.FieldF0 != carrier.FieldF0);

				// The WIDER position measure: is the carrier at the line's MINIMUM captured sequence? The strict test
				// below drops 17 of the 31 lines for want of a full order, which is most of the evidence - and a
				// sequence of 0 is a real captured value on this table, not a missing one. Counted separately rather
				// than by loosening the strict one, so the two can be compared.
				// Gated on min != max, not merely on more than one step. 14 of the 31 lines have every captured
				// step at the same sequence, and on those "at the lowest" and "at the highest" are both trivially
				// true - counting them inflates both columns and makes the finding look like a wash.
				var all = byLine[carrier.QuestLineId];
				var lo = all.Min(q => q.JournalSequence);
				var hi = all.Max(q => q.JournalSequence);
				if (lo != hi)
				{
					atMin += carrier.JournalSequence == lo ? 1 : 0;
					atMax += carrier.JournalSequence == hi ? 1 : 0;
					multiStep++;
				}

				// Position, only where every step of the line carries a captured sequence.
				var steps = byLine[carrier.QuestLineId];
				if (steps.Count > 1 && steps.All(q => q.JournalSequence > 0))
				{
					var order = steps.OrderBy(q => q.JournalSequence).Select(q => q.QuestId).ToList();
					if (order[0] == carrier.QuestId)
						first++;
					else if (order[^1] == carrier.QuestId)
						last++;
					else
						middle++;
				}
				else
				{
					unordered++;
				}

				if (rows.Count < 12)
				{
					rows.Add($"    line {carrier.QuestLineId,-6} quest {carrier.QuestId,-6} @f0 {carrier.FieldF0,-8} @f8 {carrier.FieldF8,-8}" +
					         $"  catalog xp {chapter.XpReward,-8} cash {chapter.CashReward,-8}  {(xpOk ? "xp=" : "xp≠")}{(cashOk ? " cash=" : " cash≠")}");
				}
			}

			Console.WriteLine();
			Console.WriteLine("--- Quest_c @+0xf0/@+0xf8 vs the catalog's LINE reward (#759) ---");
			if (carriers == 0)
			{
				Console.WriteLine("  no shipped quest carries @+0xf0 - import the capture corpus first.");
				return;
			}

			Console.WriteLine($"  {carriers} quest(s) carry the pair; {noChapter} sit on a line the catalog does not hold, " +
			                  $"{chapterSilent} on a line whose catalog reward is 0");
			Console.WriteLine($"  @+0xf0 == MainQuest.xp_reward    {xpAgree,4} / {carriers}");
			Console.WriteLine($"  @+0xf8 == MainQuest.cash_reward  {cashAgree,4} / {carriers}");
			Console.WriteLine($"  constancy across CAPTURED siblings: {repeated} repeat the value, {contradicted} carry a different one");
			Console.WriteLine($"  carrier position in its line (fully-ordered lines only): first {first}, last {last}, middle {middle}; {unordered} line(s) not fully ordered");
			Console.WriteLine($"  ⭐ over the {multiStep} carriers whose line spans MORE THAN ONE sequence: {atMin} sit at its lowest, {atMax} at its highest");
			Console.WriteLine("     - and `XpReward` is the other way round (the LAST step, 25 of 26), so the two pairs are");
			Console.WriteLine("       positionally COMPLEMENTARY: one on the step you are offered, one on the step that pays.");
			foreach (var r in rows)
				Console.WriteLine(r);

			// where the catalog's reward field actually lives. It is populated on 23 of 2,181 lines, so "the carrier's
			// line has no catalog reward" could be a coverage artefact rather than a disagreement - printing the whole
			// populated set is what tells the two apart, and it is short enough to print in full.
			var paying = catalog.MainQuests.Where(m => m.XpReward != 0 || m.CashReward != 0).DistinctBy(m => m.TemplateId).OrderBy(m => m.TemplateId).ToList();
			Console.WriteLine($"  the catalog's own reward field, in full ({paying.Count} line(s) of {lines.Count}):");
			foreach (var m in paying)
			{
				var ours = byLine.TryGetValue(m.TemplateId, out var steps) ? steps.Count : 0;
				var carrier = ours == 0 ? null : steps!.FirstOrDefault(q => q.FieldF0 != 0);
				Console.WriteLine($"    line {m.TemplateId,-6} xp {m.XpReward,-8} cash {m.CashReward,-8} '{m.Name}'  " +
				                  $"(we hold {ours} step(s), @+0xf0 carrier {(carrier == null ? "none" : carrier.QuestId.ToString())})");
			}

			TwoPairExclusion(quests.Select(q => (q.QuestId, q.QuestLineId, q.XpReward, q.MoneyReward, q.FieldF0, q.FieldF8, q.HasSnapshotRecord)));
		}

		// Are the two reward pairs the same quantity in two slots?
		//
		// The standing reading is that they are two different rewards at opposite ends of a line - `@+0xf0`/`@+0xf8`
		// on the step you are offered, `@+0x28`/`@+0x20` on the step that pays - and that reading predicts a line
		// carrying both. Measured over the captured lines, that is not what the corpus holds: a line carries one pair
		// or the other, essentially never both, and on the single line that does carry both the XP values are equal.
		//
		// Read the exclusion against CHANCE, not against zero. 31 quests carry the `@+0xf0` pair and 21 the
		// journal's, over 100 captured quests, so ~6.5 overlaps is what independence predicts - which is why the
		// expected count is printed beside the observed one. A single overlap is a real anti-correlation; calling it
		// "almost none" without the base rate would be the same mistake as reading a purity sweep without one.
		private static void TwoPairExclusion(IEnumerable<(uint QuestId, uint LineId, uint Xp, ulong Money, uint F0, ulong F8, bool Captured)> rows)
		{
			var all = rows.ToList();
			var captured = all.Where(q => q.Captured).ToList();

			var bothQ = all.Count(q => q.F0 != 0 && q.Xp != 0);
			var f0Only = all.Count(q => q.F0 != 0 && q.Xp == 0);
			var xpOnly = all.Count(q => q.F0 == 0 && q.Xp != 0);
			var f0Any = bothQ + f0Only;
			var xpAny = bothQ + xpOnly;
			var expected = captured.Count == 0 ? 0 : (double)f0Any * xpAny / captured.Count;

			Console.WriteLine();
			Console.WriteLine("--- are @+0xf0/@+0xf8 and @+0x28/@+0x20 the SAME pair in two slots? (#802) ---");
			Console.WriteLine($"  per QUEST over the {captured.Count} captured: @+0xf0 only {f0Only}, @+0x28 only {xpOnly}, BOTH {bothQ}" +
			                  $"  (chance predicts {expected:0.0} overlaps)");

			// Per line, which is the measure the "opposite ends of one line" reading actually makes a claim about:
			// two rewards at the two ends of a line would put both on almost every reward-bearing line.
			var lines = all.GroupBy(q => q.LineId)
				.Where(g => g.Any(q => q.Captured))
				.Select(g => (Line: g.Key, HasF0: g.Any(q => q.F0 != 0), HasXp: g.Any(q => q.Xp != 0)))
				.ToList();
			var lf0 = lines.Count(l => l.HasF0 && !l.HasXp);
			var lxp = lines.Count(l => !l.HasF0 && l.HasXp);
			var lboth = lines.Count(l => l.HasF0 && l.HasXp);
			Console.WriteLine($"  per LINE  over the {lines.Count} captured: @+0xf0 only {lf0}, @+0x28 only {lxp}, BOTH {lboth}" +
			                  "   <- the 'one at each end of a line' reading predicts BOTH on nearly every line");

			// And what the overlap says where it exists. This is the whole of the positive evidence, so it prints the
			// rows rather than a verdict: one row is one row, however clean it looks.
			foreach (var q in all.Where(q => q.F0 != 0 && q.Xp != 0))
				Console.WriteLine($"    quest {q.QuestId} (line {q.LineId}): @+0xf0 {q.F0} vs @+0x28 {q.Xp} {(q.F0 == q.Xp ? "EQUAL" : "differ")}" +
				                  $" | @+0xf8 {q.F8} vs @+0x20 {q.Money} {(q.F8 == q.Money ? "EQUAL" : "differ")}");
		}

		// No field does: `Quest.tier` and `Quest.start_new_tier` are zero on all 5042 rows. But a protobuf's repeated
		// field preserves the order it was written in, and the journal has to get its order from somewhere the client
		// can read offline - the completed-quest journal renders the steps of a line in order with no server input
		// beyond the ids. So the hypothesis is positional: the Nth `Quest` row of a chapter is its Nth step.
		//
        // The captured `JournalSequence` is the ground truth to score it against (harvested from the FullChar
		// snapshot's completed-quest journal, which ships an explicit `[seq][questId]` pair per step). Only lines
		// where every step carries a captured sequence are scored - a partially-covered line cannot falsify anything.
		//
		// Two CONTROLS run beside it, because a hypothesis that cannot fail is not evidence. Ascending template id
		// is the null hypothesis (if it scored the same, row order would be telling us nothing that sorting already
		// does), and reversed row order catches a corpus so degenerate that any total order fits it.
		private static async Task StepOrderCheck(QuestGiverList catalog)
		{
			await using var db = OpenDb();
			var captured = db.QuestDefinitions
				.Where(q => q.JournalSequence > 0)
				.Select(q => new { q.QuestId, q.QuestLineId, q.JournalSequence })
				.AsEnumerable()
				.GroupBy(q => q.QuestLineId)
				.ToDictionary(g => g.Key, g => g.ToDictionary(q => q.QuestId, q => q.JournalSequence));

			// First occurrence wins, so a step listed twice keeps the position of its first row - the same rule the
			// tree print uses, and the only one under which "the Nth row" is well defined.
			var byChapter = catalog.Quests
				.Where(q => q.MainQuestTemplateId != 0)
				.GroupBy(q => q.MainQuestTemplateId)
				.ToDictionary(g => g.Key, g => g.Select(q => q.TemplateId).Distinct().ToList());

			int scored = 0, rowOrder = 0, idOrder = 0, reversed = 0, multiStep = 0;
			var mismatches = new List<string>();

			// A 2-step line is a coin flip, so a headline percentage over a corpus that is mostly 2-step lines says more
			// about the corpus than about the ordering. Bucketing by length is what separates the two.
			var buckets = new SortedDictionary<int, (int Lines, int Row, int Id)>();

			foreach (var (lineId, steps) in byChapter)
			{
				if (!captured.TryGetValue(lineId, out var seq) || steps.Count < 2)
					continue;

				// Every step of the line must be covered, and the captured set must be the catalog set - otherwise a
				// missing step silently shifts every position after it and the comparison measures the gap, not the order.
				if (steps.Any(s => !seq.ContainsKey(s)) || seq.Count != steps.Count)
					continue;

				scored++;
				multiStep += steps.Count;
				var truth = steps.OrderBy(s => seq[s]).ToList();

				if (steps.SequenceEqual(truth))
					rowOrder++;
				else if (mismatches.Count < 5)
					mismatches.Add($"    line {lineId}: rows [{string.Join(",", steps)}] vs captured [{string.Join(",", truth)}]");

				var byId = steps.OrderBy(s => s).SequenceEqual(truth);
				if (byId)
					idOrder++;
				if (Enumerable.Reverse(steps).SequenceEqual(truth))
					reversed++;

				var key = Math.Min(steps.Count, 6);
				var b = buckets.TryGetValue(key, out var cur) ? cur : default;
				buckets[key] = (b.Lines + 1, b.Row + (steps.SequenceEqual(truth) ? 1 : 0), b.Id + (byId ? 1 : 0));
			}

			Console.WriteLine();
			Console.WriteLine("--- step order within a line: catalog ROW ORDER vs the captured JournalSequence ---");
			if (scored == 0)
			{
				Console.WriteLine("  no fully-covered multi-step line to score - import the capture corpus first.");
				return;
			}

			Console.WriteLine($"  scored {scored} fully-covered multi-step line(s), {multiStep} steps");
			Report("catalog row order", rowOrder);
			Report("ascending template id  (control)", idOrder);
			Report("reversed row order     (control)", reversed);
			foreach (var m in mismatches)
				Console.WriteLine(m);

			Console.WriteLine("  by line length (a 2-step line is a coin flip; the long ones are where a real order shows):");
			foreach (var (len, b) in buckets)
			{
				var label = len == 6 ? "6+" : len.ToString();
				Console.WriteLine($"    {label,-3} steps  {b.Lines,4} line(s)   row order {b.Row,4} ({100.0 * b.Row / b.Lines,5:0.0}%)" +
				                  $"   ascending id {b.Id,4} ({100.0 * b.Id / b.Lines,5:0.0}%)");
			}

			var unscored = byChapter.Count(c => c.Value.Count > 1) - scored;
			Console.WriteLine($"  multi-step lines with no full captured order to score against: {unscored}");
			return;

			void Report(string label, int hits)
			{
				Console.WriteLine($"  {label,-34} {hits,5} / {scored,-5} ({100.0 * hits / scored,5:0.0}%)");
			}
		}

		// One TSV line per record. These double as the identity used to tell a harmlessly-repeated id from one that
		// carries two different definitions, so they must cover every field the report reads - a projection that
		// dropped a column would make two differing rows look identical.
		private static string ChapterRow(QuestGiverList.MainQuest m)
		{
			return $"{m.TemplateId}\t{m.Hash}\t{m.Tier}\t{m.FirstQuestTemplateId}\t{m.LastQuestTemplateId}\t{m.PrevTemplateId}\t" +
			       $"{m.NextTemplateId}\t{m.ConditionScriptId}\t{m.XpReward}\t{m.CashReward}\t" +
			       $"{string.Join(";", m.Rewards.Select(r => $"{r.TemplateId}:{r.Type}"))}\t" +
			       $"{string.Join(";", m.OptionalRewards.Select(r => $"{r.TemplateId}:{r.Type}"))}\t{Flatten(m.Name ?? "")}";
		}

		private static string QuestRow(QuestGiverList.Quest q)
		{
			return $"{q.TemplateId}\t{q.Hash}\t{q.MainQuestTemplateId}\t{q.StartScryScriptId}\t{q.Tier}\t{q.StartNewTier}\t{Flatten(q.Name ?? "")}";
		}

		private static string GoalRow(QuestGiverList.QuestGoal g)
		{
			return $"{g.TemplateId}\t{g.Hash}\t{g.QuestTemplateId}\t{g.Phase}\t{Flatten(g.Name ?? "")}";
		}

		// The record walked as raw protobuf - `(field number, wire type)` tallied per submessage - rather than read
		// through the schema. Two things only this can answer:
		//
		//  1. Absent vs zero. protobuf-net generates `get => __pbn__X.GetValueOrDefault()`, so an unset optional
		//     and a zero are indistinguishable through the parsed object. Counting the bytes separates them:
		//     `MainQuest.condition_script_id` and `Quest.start_scry_script_id` are never EMITTED, while
		//     `MainQuest.type` / `Quest.tier` / `Quest.start_new_tier` ride on every row carrying zero. Reading an
		//     absent field as a zero is how a flag gets a meaning it does not have.
		//  2. Whether anything here goes unparsed. A schema-driven read silently skips a field number it does not
		//     declare; this cross-checks every observed tag against the tags the generated classes declare, and
		//     reports the leftover bytes. That is what makes "the layout is fully known" a measurement rather than an
		//     assumption - and what says the catalog cannot supply a quest goal's missing verb/target/count:
		//     a QuestGoal is fields 1-5 with no unaccounted space for them to hide in.
		private static void RawFieldCensus(string rdbDir)
		{
			var payload = ReadQuestCatalogPayload(rdbDir, ReportRdbProxy());
			if (payload == null)
				return;

			// Field number of each repeated list on QuestGiverList, paired with the element type whose declared tags
			// it is checked against. Read off the generated classes, so a schema change cannot leave this stale.
			(int Field, string Name, Type Element)[] lists =
			[
				(2, "quest_giver", typeof(QuestGiverList.QuestGiver)),
				(3, "main_quest", typeof(QuestGiverList.MainQuest)),
				(4, "quest", typeof(QuestGiverList.Quest)),
				(5, "quest_giver_pos", typeof(QuestGiverList.QuestGiverPos)),
				(6, "quest_goal", typeof(QuestGiverList.QuestGoal))
			];

			var top = new SortedDictionary<(int Field, int Wire), int>();
			var sub = lists.ToDictionary(l => l.Name, _ => new SortedDictionary<(int Field, int Wire), int>());
			var rows = lists.ToDictionary(l => l.Name, _ => 0);
			var unparseable = 0;

			var trailing = WalkFields(payload, (field, wire, body) =>
			{
				top.TryGetValue((field, wire), out var n);
				top[(field, wire)] = n + 1;

				var list = lists.FirstOrDefault(l => l.Field == field);
				if (list.Name == null || wire != 2)
					return;

				rows[list.Name]++;
				if (WalkFields(body, (f, w, _) =>
				    {
					    sub[list.Name].TryGetValue((f, w), out var c);
					    sub[list.Name][(f, w)] = c + 1;
				    }) != 0)
					unparseable++;
			});

			Console.WriteLine();
			Console.WriteLine("--- raw field census (the bytes, not the schema: absent vs zero, and anything unparsed) ---");
			Console.WriteLine($"  top level  {string.Join(", ", top.Select(kv => $"field {kv.Key.Field}/wt{kv.Key.Wire} x{kv.Value}"))}");
			Console.WriteLine($"  trailing bytes {trailing}, submessages that did not parse to their own end: {unparseable}" +
			                  (trailing == 0 && unparseable == 0 ? "   <-- the record is fully accounted for" : "   ⚠️  UNDECODED REGION"));

			foreach (var (_, name, element) in lists)
			{
				if (rows[name] == 0)
					continue;

				// The tags the generated class declares. An observed tag missing from this set is a field we parse
				// straight past; a declared tag missing from the observed set is one the shipped data never sends.
				var declared = element.GetProperties()
					.SelectMany(p => p.GetCustomAttributes(typeof(ProtoBuf.ProtoMemberAttribute), true))
					.Cast<ProtoBuf.ProtoMemberAttribute>()
					.Select(a => a.Tag)
					.ToHashSet();
				var observed = sub[name].Keys.Select(k => k.Field).ToHashSet();

				Console.WriteLine();
				Console.WriteLine($"  {name} x{rows[name]}");
				foreach (var ((field, wire), count) in sub[name])
				{
					var note = declared.Contains(field)
						? count == rows[name] ? "  on every row" : ""
						: "   ⚠️  UNDECLARED - parsed straight past";
					Console.WriteLine($"    field {field,-3} wt {wire}  x{count,-6}{note}");
				}

				var never = declared.Except(observed).OrderBy(t => t).ToList();
				if (never.Count > 0)
					Console.WriteLine($"    declared but NEVER EMITTED: {string.Join(", ", never)}  (absent, not zero)");
			}
		}

		// Walk a protobuf buffer field by field, calling `onField` for each. Returns the number of TRAILING bytes -
		// 0 means the buffer parsed exactly to its end, anything else means the grammar is wrong. Deliberately local
		// to this report: it is a diagnostic over shipped bytes, not a wire path.
		private static int WalkFields(ReadOnlySpan<byte> body, ReadFieldCallback onField)
		{
			var i = 0;
			while (i < body.Length)
			{
				var start = i;
				if (!TryReadVarint(body, ref i, out var key))
					return body.Length - start;

				var field = (int)(key >> 3);
				var wire = (int)(key & 7);
				switch (wire)
				{
					case 0:
					{
						if (!TryReadVarint(body, ref i, out _))
							return body.Length - start;
						onField(field, wire, default);
						break;
					}

					case 1:
					case 5:
					{
						var fixedWidth = wire == 1 ? 8 : 4;
						if (i + fixedWidth > body.Length)
							return body.Length - start;
						onField(field, wire, body.Slice(i, fixedWidth));
						i += fixedWidth;
						break;
					}

					case 2:
					{
						if (!TryReadVarint(body, ref i, out var length) || i + (int)length > body.Length)
							return body.Length - start;
						onField(field, wire, body.Slice(i, (int)length));
						i += (int)length;
						break;
					}

					default:
					{
						return body.Length - start; // group wire types: not used by any of these messages
					}
				}
			}

			return 0;
		}

		private delegate void ReadFieldCallback(int field, int wire, ReadOnlySpan<byte> body);

		private static bool TryReadVarint(ReadOnlySpan<byte> body, ref int i, out ulong value)
		{
			value = 0;
			for (var shift = 0; shift < 64; shift += 7)
			{
				if (i >= body.Length)
					return false;

				var b = body[i++];
				value |= (ulong)(b & 0x7f) << shift;
				if ((b & 0x80) == 0)
					return true;
			}

			return false;
		}

		// The record's protobuf payload - everything after the 12-byte RDB-Id header. Null when the client's RDB has
		// no such entry.
		//
		// Takes the CDN base EXPLICITLY rather than reaching for it through OpenRdb. OpenRdb resolves the
		// proxy off ReportCli's static `_options`, which only the `--report` entry point ever assigns - so this
		// function, which is deliberately shared with the import module, threw a NullReferenceException on every
		// import run and took the whole `rdb-quest-catalog` module (and everything sequenced after it) down with it.
		// Every other RDB importer already takes the base as a parameter; this is that same shape.
		private static byte[]? ReadQuestCatalogPayload(string rdbDir, string? cdnBase)
		{
			using var rdb = new RdbReader(rdbDir, cdnBase);
			if (!rdb.TryGet(QuestCatalogRdbType, 0, out var entry))
				return null;

			var raw = rdb.ReadDecompressed(entry);
			return raw.Length <= QuestCatalogHeaderBytes
				? null
				: raw[QuestCatalogHeaderBytes..];
		}

		// Shared with the importer module so the RDB type, the header width and the protobuf entry point are
		// stated once. Returns null when the client's RDB has no such entry.
		internal static QuestGiverList? ReadQuestCatalog(string rdbDir, string? cdnBase)
		{
			var payload = ReadQuestCatalogPayload(rdbDir, cdnBase);
			if (payload == null)
				return null;

			using var ms = new MemoryStream(payload);
			return ProtoBuf.Serializer.Deserialize<QuestGiverList>(ms);
		}
	}
}
