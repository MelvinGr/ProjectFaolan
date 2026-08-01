using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Faolan.Core.Protobuf;
using Faolan.Core.Rdb;

namespace Faolan.Importer.Cli
{
	// `--report rdb-quest-catalog` - the client's own quest catalog, RDB type 1000104 (#568).
	//
	// This is the `RDBQUESTGIVERS` data table the client prints at startup: a 12-byte RDB header followed by a raw
	// `QuestGiverList` protobuf, whose schema this repo already carried in `Faolan.Core/Protobuf` but had never read.
	// It is the DEFINITION side of the quest system - the wire only ever shows the quests a captured character
	// happened to hold, while this ships every quest in the game, with the designers' names in PLAINTEXT (no LDB
	// page, no captured decryption key).
	//
	// The client's model is MainQuest (a quest LINE) -> Quest (a step) -> QuestGoal, and it maps onto our tables
	// one-for-one: `QuestLine.QuestLineId` IS `MainQuest.template_id`, `QuestDefinition.QuestId` IS `Quest.template_id`,
	// and `QuestLine.Magic` IS `MainQuest.hash` (the ACGHash line token). That last one is what VERIFIES this
	// report rather than merely asserting it - see the `hash` cross-check the summary prints.
	//
	// ⚠️ Several right-shaped fields in the schema are DEAD in the shipped data, and the census below prints the
	// populated count for every one of them precisely so nobody plans a decode around a column of zeroes. Known
	// empties: `MainQuest.type`, `Quest.tier`, `Quest.start_new_tier`, and both quest-giver lists.
	public static partial class ReportCli
	{
		// RDB type of the RDBQUESTGIVERS data table, and the fixed header the payload sits behind. The header is
		// why a protobuf parse at offset 0 fails - the first sweep for this table reported no hits for that reason.
		private const uint QuestCatalogRdbType = 1000104;
		private const int QuestCatalogHeaderBytes = 12;

		// The four line tokens `docs/re/questc-region-decode.md` recovered from CAPTURES, kept here as an
		// independent check on the table: they were read off the wire, the hashes below come out of the client's
		// data file, and nothing connects the two but the claim that our QuestLineId is the client's MainQuest id.
		private static readonly (uint LineId, string Token)[] CapturedLineTokens =
			[(241, "W6QT"), (1054, "RRL3"), (1772, "B0DN"), (1834, "3734")];

		private static void RdbQuestCatalog(string rdbDir, string? arg)
		{
			var wanted = ParseIdFilter(arg);
			var catalog = ReadQuestCatalog(rdbDir);
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

			// A repeated id is NOT noise to be deduped away silently: 2 chapters and 15 goals have duplicate ids whose
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
			Console.WriteLine("  ⚠️  ordering is between LINES. Step order WITHIN a chapter is not in this table -");
			Console.WriteLine("      Quest.tier and Quest.start_new_tier, the fields for exactly that, are empty (census above).");

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

					// Distinct ROWS, not distinct ids: an id repeated with identical content is one definition listed
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

		// One TSV line per record. These double as the IDENTITY used to tell a harmlessly-repeated id from one that
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

		// Shared with the importer module so the RDB type, the header width and the protobuf entry point are
		// stated once. Returns null when the client's RDB has no such entry.
		internal static QuestGiverList? ReadQuestCatalog(string rdbDir)
		{
			using var rdb = new RdbReader(rdbDir);
			if (!rdb.TryGet(QuestCatalogRdbType, 0, out var entry))
				return null;

			var raw = rdb.ReadDecompressed(entry);
			if (raw.Length <= QuestCatalogHeaderBytes)
				return null;

			using var ms = new MemoryStream(raw, QuestCatalogHeaderBytes, raw.Length - QuestCatalogHeaderBytes);
			return ProtoBuf.Serializer.Deserialize<QuestGiverList>(ms);
		}
	}
}
