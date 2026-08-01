using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Faolan.Core.Crypt;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.Quest;

namespace Faolan.Importer.Extractors
{
	// Harvests quest journal-content definitions (QuestDefinition + QuestTextRef rows) from captured QuestFullUpdate
	// (0x63268258) and QuestJournal (0x79f8c7a1) packets. Grammar + round-trip proof: tools/capture/decode_quest.py;
	// builders: WorldQuest.BuildQuestJournal/BuildQuestFullUpdate (byte-exact golden tests). Construct, don't replay:
	// we keep ids + the deterministic per-(id,category) content keys, never literal text. A quest's full slot set
	// needs both packets - the full-update carries title/summary/objective/headers, the journal carries the list
	// sub-line (category 50303) - so we merge them by quest id.
	public static class QuestDefinitionExtractor
	{
		public static List<QuestDefinition> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var byQuest = new Dictionary<uint, QuestDefinition>();

			// The journal's ACGHash, keyed by the AREA it belongs to (#544). Collected across the whole sweep and
			// applied at the end rather than as each journal arrives, for two reasons: it is a property of the area, so
			// it belongs to every quest filed under it and not just the ones whose journal LINE was captured; and a
			// journal seen before that quest's full-update would otherwise find no definition to write to.
			var hashByArea = new Dictionary<uint, uint>();

			foreach (var p in packets)
			{
				if (!p.IsDownload)
					continue;

				if (p.SubOpcode == QuestFullUpdateCodec.Opcode)
				{
					// TryParse is false only for a body the capture cut short - there is no scan to fall back on any
					// more, so such a packet contributes nothing rather than a half-read definition (#540).
					if (p.TryParse(QuestFullUpdateCodec.Parse, out var full))
						MergeFullUpdate(full.Record, byQuest);
				}
				else if (p.SubOpcode == QuestJournalCodec.Opcode)
				{
					if (p.TryParse(QuestJournalCodec.Parse, out var list))
						MergeJournal(list, byQuest, hashByArea);
				}
				else if (SnapshotJournalReader.TryRead(p.Bytes, out _, out var journal))
				{
					foreach (var record in journal)
						MergeSnapshotRecord(record, byQuest);
				}
			}

			foreach (var def in byQuest.Values)
			{
				if (hashByArea.TryGetValue(def.QuestLineId, out var hash))
					def.IndexMagic = hash;
			}

			// Only keep quests whose full-update was seen (it carries the bulk of the slots + the marker).
			return byQuest.Values.Where(q => q.TextRefs.Any(r => r.Slot == QuestTextSlot.Title)).ToList();
		}

		private static string? Attr(string xml, string name)
		{
			var m = Regex.Match(xml, name + "=\"([^\"]*)\"");
			return m.Success
				? m.Groups[1].Value
				: null;
		}

		private static uint AttrUInt(string xml, string name)
		{
			var v = Attr(xml, name);
			return v != null && uint.TryParse(v, out var n)
				? n
				: 0u;
		}

		private static QuestDefinition GetOrAdd(Dictionary<uint, QuestDefinition> byQuest, uint questId)
		{
			if (!byQuest.TryGetValue(questId, out var def))
			{
				byQuest[questId] = def = new QuestDefinition
				{
					QuestId = questId
				};
			}

			return def;
		}



		// Reads the full-update ENTIRELY off `QuestFullUpdateCodec`'s `QuestcRecord` (#540). It used to scan the raw
		// body for `<remoteformat>`/`<localized>` records and read the level word and the marker block at offsets
		// RELATIVE to where the primary objective record landed, alongside the codec's own parse of the same bytes.
		// Every value the scan derived was proven identical to the record's over all 74 captured full-updates before
		// the scan went - EXCEPT the markers, which the scan got WRONG (see below).
		//
		// ⚠️ The level is the record's `state` slot: with the `[u32 len]` prefix `CapturedPacket.Body` keeps, the
		// scan's `body[33..37]` is `state`, not `Field114` (which is 0 on every captured full-update).
		private static void MergeFullUpdate(QuestcRecord record, Dictionary<uint, QuestDefinition> byQuest)
		{
			// A full-update carries ONE 50304 objective record PER GOAL, in goal order - reading only the first is how
			// two-goal quests came to look like one-goal quests (12369 dropped 7134, 10287 dropped 7363; the corpus
			// also has a three-goal quest, 6499/6514/6515, and a twelve-goal one, 19314).
			var goals = record.Objectives?.Goals ?? [];
			var title = TextRef(record.Title);
			var areaTitle = TextRef(record.Area);
			var objective = goals.Count > 0
				? TextRef(goals[0].Objective)
				: null;

			if (title == null || areaTitle == null || objective == null)
				return; // not a well-formed full-update

			var def = GetOrAdd(byQuest, record.QuestId);
			def.QuestLineId = record.QuestLineId;
			def.GoalId = objective.Id;
			def.Level = record.Level;

			var (region, map) = RegionAndMap(record);
			if (map != null)
				def.MapId = map.Id;

			// The definition's marker is the PRIMARY goal's - the quest pin, not a per-goal one.
			def.MarkerX = goals[0].MarkerX;
			def.MarkerY = goals[0].MarkerY;

			SetRef(def, QuestTextSlot.Title, title);
			SetRef(def, QuestTextSlot.Summary, TextRef(record.Summary));
			SetRef(def, QuestTextSlot.QuestLineTitle, areaTitle);
			SetRef(def, QuestTextSlot.RegionName, region);
			SetRef(def, QuestTextSlot.MapName, map);
			SetRef(def, QuestTextSlot.Objective, objective);
			SetRef(def, QuestTextSlot.ObjectiveLocalized, goals.Select(g => TextRef(g.TextRecord)).FirstOrDefault(r => r is { Tag: "localized" }));

			// Emit each goal as a QuestGoalDefinition from its own objective record - real per-goal map/marker/
			// objective-ref, not a hardcoded seed. The importer's Seed* overlays later find-or-create by GoalId, so
			// this completes with the server-side-only fields (TargetName/GoalType/QuestItemId) rather than
			// duplicating them.
			//
			// ⚠️ EVERY goal carries its OWN map pin, which the byte scan could not see: it read one marker block, the
			// one sitting before the first objective record, and gave it to the primary goal only. Quest 10238's two
			// goals are pinned at (416,701) and (521,757), 12369's at (524,755) and (574,944), 14020's three all
			// differently - so every secondary goal in the DB carried no pin at all. The scan also DISCARDED the
			// primary's pin whenever the goal's map differed from the quest's, because it gated the block on the
			// quest's 52000 map id: quest 10287's goal 400 is on map 4020 while the quest reads 4010, so its
			// (933,715) was thrown away.
			for (var i = 0; i < goals.Count; i++)
			{
				var goal = goals[i];
				if (TextRef(goal.Objective) is not { } text)
					continue;

				// The goal's reserved words - up to three (marker, identity) reference slots, see
				// QuestObjectiveGoal.Reserved and #546. They are the goal's own data and cannot be derived, so a quest
				// the captures only ever full-updated (no snapshot journal, where ObjectiveGoalExtractor harvests them)
				// would otherwise carry zeros - and the live sender used to fill the [5..6] slot with a clock reading
				// instead, which retail never sends (#537).
				var row = UpsertGoal(def, goal, text, (uint)(i + 1), i > 0);
				if (goal.Reserved.Length == ReservedWords)
					SetReserved(row, goal.Reserved);
			}
		}

		// A Quest_c record's two category-52000 refs are region (the smaller id, e.g. 4) and map (the map id). Told
		// apart by id rather than by position, so the full-update and the snapshot journal agree. MapId matters beyond
		// the text: it is an FK to Maps, and the quests module SKIPS any definition whose map is not in the catalog.
		private static (TextRecord? Region, TextRecord? Map) RegionAndMap(QuestcRecord record)
		{
			var r1 = TextRef(record.Region1);
			var r2 = TextRef(record.Region2);
			if (r1 == null || r2 == null)
				return (r1 ?? r2, null);

			return r1.Id <= r2.Id
				? (r1, r2)
				: (r2, r1);
		}

		private const int ReservedWords = 7;

		private static void SetReserved(QuestGoalDefinition row, uint[] reserved)
		{
			row.Extra0 = reserved[0];
			row.Extra1 = reserved[1];
			row.Extra2 = reserved[2];
			row.Extra3 = reserved[3];
			row.Extra4 = reserved[4];
			row.Extra5 = reserved[5];
			row.Extra6 = reserved[6];
		}

		// The journal LIST (0x79f8c7a1) carries the one slot a full-update does not: the panel's per-quest sub-line
		// (category 50303). The RRL3 scan + per-entry offset walk this used to do is QuestJournalCodec's grammar.
		//
		// It also carries the area's ACGHash, which the journal builder needs back (#544). That one is recorded per
		// AREA into `hashByArea` and applied to every definition at the end of the sweep - see Extract.
		private static void MergeJournal(QuestJournalIIR list, Dictionary<uint, QuestDefinition> byQuest, Dictionary<uint, uint> hashByArea)
		{
			foreach (var area in new[] { list.Area }.Concat(list.MoreAreas))
			{
				if (area.AcgHash != 0)
					hashByArea[area.QuestLineId] = area.AcgHash;

				foreach (var entry in area.Entries)
				{
					if (TextRef(entry.SubLine) is { Category: 50303 } subLine && byQuest.TryGetValue(entry.QuestId, out var def))
						SetRef(def, QuestTextSlot.ListSubLine, subLine);
				}
			}
		}

		// One Quest_c journal record -> a definition. This is the ONLY source with journal content for a quest the
		// captures never showed a QuestFullUpdate for: the login snapshot carries a record for every HELD quest, so a
		// level-80 character's whole J-panel is here (NecroL80: 27 records vs 2 full-updates). It is also the only
		// source of the record SCALARS, which is what makes a quest snapshot-eligible - i.e. what lets the server
		// regenerate its journal entry instead of leaving the panel empty.
		private static void MergeSnapshotRecord(QuestcRecord record, Dictionary<uint, QuestDefinition> byQuest)
		{
			var title = TextRef(record.Title);
			var areaTitle = TextRef(record.Area);
			if (title == null || areaTitle == null)
				return; // not a well-formed record

			var def = GetOrAdd(byQuest, record.QuestId);
			def.QuestLineId = record.QuestLineId;
			def.HasSnapshotRecord = true;

			// The record's state word IS the quest level (see QuestDefinition.Level). Do not clobber a value already
			// harvested from a QuestFullUpdate - they agree where both exist, and the full-update is the older source.
			if (def.Level == 0)
				def.Level = record.Level;

			def.SnapshotQuestLineFlags = record.QuestLineFlags;
			def.SnapshotUnknown118 = record.Field118;
			def.SnapshotUnknownEc = record.FieldEc;
			def.SnapshotXpReward = record.XpReward;
			def.SnapshotCashReward = record.CashReward;
			def.SnapshotQuestLineReward = record.QuestLineReward;
			def.SnapshotChainHeadQuestId = record.Field2c;

			var (region, map) = RegionAndMap(record);

			SetRef(def, QuestTextSlot.Title, title);
			SetRef(def, QuestTextSlot.Summary, TextRef(record.Summary));
			SetRef(def, QuestTextSlot.QuestLineTitle, areaTitle);
			SetRef(def, QuestTextSlot.RegionName, region);
			SetRef(def, QuestTextSlot.MapName, map);
			if (map != null && def.MapId == 0)
				def.MapId = map.Id;
		}

		private static void SetRef(QuestDefinition def, QuestTextSlot slot, TextRecord? rec)
		{
			if (rec == null)
				return;

			var existing = def.TextRefs.FirstOrDefault(r => r.Slot == slot);
			if (existing == null)
			{
				def.TextRefs.Add(existing = new QuestTextRef
				{
					Slot = slot
				});
			}

			existing.RefId = rec.Id;
			existing.Category = rec.Category;
			existing.KeyText = LdbKey.Decode(rec.Key) ?? new();
			existing.IsLocalized = rec.Tag == "localized";
			existing.Flags = rec.Flags;
		}

		// A Quest_c header string is the full <remoteformat .../> XML; the slot is identified by position in the
		// record, so only the attributes matter here.
		private static TextRecord? TextRef(string xml)
		{
			if (string.IsNullOrEmpty(xml))
				return null;

			var key = Attr(xml, "key");
			return key == null
				? null
				: new TextRecord(xml.Contains("<localized", StringComparison.Ordinal)
					? "localized"
					: "remoteformat", AttrUInt(xml, "id"), AttrUInt(xml, "category"), key, Attr(xml, "flags")!);
		}

		// Find-or-create the goal by GoalId within the quest and set the WIRE fields off the goal RECORD. Leaves the
		// server-side-only fields (TargetName/GoalType/QuestItemId) untouched so a later Seed* overlay can complete
		// them.
		//
		// `sequence` is the goal's 1-based position in the full-update's objective list, and `partial` marks every
		// goal after the first (see QuestGoalDefinition.Partial). A goal the snapshot already supplied in full is
		// never downgraded - Partial only ever goes false here.
		//
		// ⚠️ The record's `GoalId` word and its objective text record's `id` are THE SAME NUMBER on all 111 captured
		// goal records, which is what lets this path and ObjectiveGoalExtractor's snapshot path merge onto one row:
		// the snapshot keys by the word, this one used to key by the text id. Read off the word now, so the two say
		// the same thing rather than agreeing by coincidence.
		private static QuestGoalDefinition UpsertGoal(QuestDefinition def, QuestObjectiveGoal record, TextRecord objective, uint sequence = 1, bool partial = false)
		{
			var goal = def.Goals.FirstOrDefault(g => g.GoalId == record.GoalId);
			if (goal == null)
			{
				def.Goals.Add(goal = new QuestGoalDefinition
				{
					GoalId = record.GoalId,
					Sequence = sequence,
					Partial = partial
				});
			}
			else if (!partial)
			{
				goal.Partial = false;
			}

			goal.ObjectiveRefId = objective.Id;
			goal.ObjectiveCategory = objective.Category;
			goal.ObjectiveKey = LdbKey.Decode(objective.Key) ?? new();

			// The goal's KIND TAG and its required amount (#548). The full-update carries both for EVERY goal, not
			// just the primary - measured against the snapshot journal, which is the source that was thought to hold
			// them alone: of the 34 goals the corpus shows in BOTH, all 34 agree on both fields, and across the
			// full-updates the tag takes five values (9/12/20/22/23) and the count four (1/3/7/10). So neither is a
			// constant this path was papering over - `RequiredCount` was seeded to a flat 1 here and `Separator` was
			// never set at all, which left every full-update-only goal reading as "kill one unnamed thing".
			goal.Separator = record.K;
			goal.RequiredCount = record.RequiredCount;

			// Every goal has its own pin, so a secondary one is set too - see the ⚠️ in MergeFullUpdate. A goal the
			// wire pins nowhere (quest 19314's twelve all read 0/0/0) keeps whatever a snapshot already gave it.
			if (record.MapId != 0 || record.MarkerX != 0 || record.MarkerY != 0)
			{
				goal.MapId = record.MapId;
				goal.MarkerX = record.MarkerX;
				goal.MarkerY = record.MarkerY;
			}

			return goal;
		}

		private sealed record TextRecord(string Tag, uint Id, uint Category, string Key, string Flags);
	}
}
