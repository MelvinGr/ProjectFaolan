using Faolan.Core.Crypt;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
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
		private const uint FullUpdateSubOpcode = (uint)GameServer0X2000Opcodes.QuestFullUpdateIIR;
		private const uint JournalSubOpcode = (uint)GameServer0X2000Opcodes.QuestJournalIIR;

		public static List<QuestDefinition> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var byQuest = new Dictionary<uint, QuestDefinition>();

			foreach (var p in packets)
			{
				if (!p.IsDownload)
					continue;

				var body = InnerBody(p.Bytes);
				if (body == null || body.Length < 24)
					continue;

				if (p.SubOpcode == FullUpdateSubOpcode)
					MergeFullUpdate(body, byQuest);
				else if (p.SubOpcode == JournalSubOpcode)
					MergeJournal(body, byQuest);
				else if (SnapshotJournalReader.TryRead(p.Bytes, out _, out var journal))
				{
					foreach (var record in journal)
						MergeSnapshotRecord(record, byQuest);
				}
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

		private static int IndexOf(byte[] haystack, byte[] needle, int from)
		{
			for (var i = from; i <= haystack.Length - needle.Length; i++)
			{
				var ok = true;
				for (var j = 0; j < needle.Length; j++)
				{
					if (haystack[i + j] != needle[j])
					{
						ok = false;
						break;
					}
				}

				if (ok)
					return i;
			}

			return -1;
		}

		// Bytes = [..8..][u32 headerLen][header][body]; body = [u32 len][u32 subop][u32 ack][u32 player][record area...].
		private static byte[]? InnerBody(byte[] bytes)
		{
			if (bytes == null || bytes.Length < 12)
				return null;

			var headerLen = (int)bytes.ReadU32Be(8);
			var start = 12 + headerLen;
			return start < bytes.Length
				? bytes[start..]
				: null;
		}

		private static void MergeFullUpdate(byte[] body, Dictionary<uint, QuestDefinition> byQuest)
		{
			// content @16: [00000000][01][u32 questId]...
			var questId = body.ReadU32Be(21);
			var records = Records(body, 16).ToList();

			var objective = records.FirstOrDefault(r => r is { Tag: "remoteformat", Category: 50304 });
			var objectiveLocalized = records.FirstOrDefault(r => r.Tag == "localized");
			var title = records.FirstOrDefault(r => r.Category == 50301);
			var summary = records.FirstOrDefault(r => r.Category == 50302);
			var areaTitle = records.FirstOrDefault(r => r.Category == 50300);
			var region52000 = records.Where(r => r.Category == 52000).ToList();
			if (title == null || areaTitle == null || objective == null)
				return; // not a well-formed full-update

			var def = GetOrAdd(byQuest, questId);
			def.AreaId = areaTitle.Id;
			def.GoalId = objective.Id;

			// The per-quest LEVEL word sits after the second questId: with the [u32 len] prefix InnerBody keeps, that is
			// body[33..37] (subop 4, ack 4, player 4, [len 4] all counted). Harvested straight off the wire, see
			// QuestDefinition.Level (the values track quest level, not the seeded Level).
			if (body.Length >= 37)
				def.Level = body.ReadU32Be(33);

			// The two 52000 refs are region (smaller id, e.g. 4) then map (the map id). Map id also gates the marker.
			var region = region52000.OrderBy(r => r.Id).FirstOrDefault();
			var map = region52000.OrderByDescending(r => r.Id).FirstOrDefault();
			if (map != null)
			{
				def.MapId = map.Id;
				// Marker block sits right before the objective record: [mapId][markerX][markerY] then 10 zero bytes.
				var mx = objective.Offset - 18;
				if (mx >= 0 && body.ReadU32Be(objective.Offset - 22) == map.Id)
				{
					def.MarkerX = body.ReadU32Be(objective.Offset - 18);
					def.MarkerY = body.ReadU32Be(objective.Offset - 14);
				}
			}

			SetRef(def, QuestTextSlot.Title, title);
			SetRef(def, QuestTextSlot.Summary, summary);
			SetRef(def, QuestTextSlot.AreaTitle, areaTitle);
			SetRef(def, QuestTextSlot.RegionName, region);
			SetRef(def, QuestTextSlot.MapName, map);
			SetRef(def, QuestTextSlot.Objective, objective);
			SetRef(def, QuestTextSlot.ObjectiveLocalized, objectiveLocalized);

			// Emit the quest's PRIMARY goal as a QuestGoalDefinition from the just-decoded objective + marker block -
			// real per-quest map/marker/objective-ref, not a hardcoded seed. The importer's Seed* overlays later
			// find-or-create by GoalId, so this completes with the server-side-only fields (TargetName/GoalType/
			// QuestItemId) and any multi-goal @609 detail rather than being duplicated. This is the goal the wire
			// carries; a quest's non-primary goals only appear in the @609 snapshot region (seeded where captured).
			UpsertGoal(def, objective.Id, def.MapId, def.MarkerX, def.MarkerY, objective);
		}

		private static void MergeJournal(byte[] body, Dictionary<uint, QuestDefinition> byQuest)
		{
			// ...[RRL3][00000000][u32 count] then per entry [u32 idx][u32 questId][u32 chapter] <title> <subLine>.
			var magic = Encoding.ASCII.GetBytes("RRL3");
			var idx = IndexOf(body, magic, 16);
			if (idx < 0)
				return;

			var off = idx + 4 + 4; // skip RRL3 + 00000000
			if (off + 4 > body.Length)
				return;

			var count = body.ReadU32Be(off);
			off += 4;

			for (uint i = 0; i < count && off + 12 <= body.Length; i++)
			{
				var questId = body.ReadU32Be(off + 4);
				off += 12; // idx + questId + chapter
				var title = ReadRecord(body, off);
				if (title == null)
					break;

				off += title.Length;
				var subLine = ReadRecord(body, off);
				if (subLine == null)
					break;

				off += subLine.Length;

				if (subLine.Category == 50303 && byQuest.TryGetValue(questId, out var def))
					SetRef(def, QuestTextSlot.ListSubLine, subLine);
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
			def.AreaId = record.AreaId;
			def.HasSnapshotRecord = true;

			// The record's state word IS the quest level (see QuestDefinition.Level). Do not clobber a value already
			// harvested from a QuestFullUpdate - they agree where both exist, and the full-update is the older source.
			if (def.Level == 0)
				def.Level = record.State;

			def.SnapshotUnknown14 = record.Unknown14;
			def.SnapshotUnknown118 = record.Field118;
			def.SnapshotUnknownEc = record.FieldEc;
			def.SnapshotUnknownF0 = record.FieldF0;
			def.SnapshotUnknownF8 = record.FieldF8;
			def.SnapshotUnknown28 = record.Field28;

			// The record's two category-52000 refs are region and map. They are told apart by id exactly as
			// MergeFullUpdate does it - smaller = region, larger = the map id - rather than by their position in the
			// record, so both sources agree. MapId matters beyond the text: it is an FK to Maps, and the quests module
			// SKIPS any definition whose map is not in the catalog.
			var r1 = TextRef(record.Region1);
			var r2 = TextRef(record.Region2);
			var region = r1 != null && r2 != null
				? r1.Id <= r2.Id
					? r1
					: r2
				: r1 ?? r2;
			var map = r1 != null && r2 != null
				? r1.Id > r2.Id
					? r1
					: r2
				: null;

			SetRef(def, QuestTextSlot.Title, title);
			SetRef(def, QuestTextSlot.Summary, TextRef(record.Summary));
			SetRef(def, QuestTextSlot.AreaTitle, areaTitle);
			SetRef(def, QuestTextSlot.RegionName, region);
			SetRef(def, QuestTextSlot.MapName, map);
			if (map != null && def.MapId == 0)
				def.MapId = map.Id;
		}

		// A <remoteformat>/<localized> slot: 00 <u8 len> <ascii xml>. Returns null if no record starts at off.
		private static TextRecord? ReadRecord(byte[] b, int off)
		{
			if (off + 2 > b.Length || b[off] != 0x00 || b[off + 2] != (byte)'<')
				return null;

			int len = b[off + 1];
			if (off + 2 + len > b.Length || b[off + 2 + len - 1] != (byte)'>')
				return null;

			var xml = Encoding.ASCII.GetString(b, off + 2, len);
			var tag = xml.Substring(1, xml.IndexOf(' ') - 1);
			var id = AttrUInt(xml, "id");
			var cat = AttrUInt(xml, "category");
			var key = Attr(xml, "key");
			var flags = Attr(xml, "flags");
			return new TextRecord(off, 2 + len, tag, id, cat, key!, flags!);
		}

		private static IEnumerable<TextRecord> Records(byte[] b, int from)
		{
			for (var off = from; off + 2 <= b.Length;)
			{
				var rec = ReadRecord(b, off);
				if (rec != null)
				{
					yield return rec;

					off += rec.Length;
				}
				else
					off++;
			}
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
				: new TextRecord(0, xml.Length, xml.Contains("<localized", StringComparison.Ordinal)
					? "localized"
					: "remoteformat", AttrUInt(xml, "id"), AttrUInt(xml, "category"), key, Attr(xml, "flags")!);
		}

		// Find-or-create the goal by GoalId within the quest and set the WIRE fields (map/marker/objective ref). Leaves
		// the server-side-only fields untouched so a later Seed* overlay can complete them.
		private static void UpsertGoal(QuestDefinition def, uint goalId, uint mapId, uint markerX, uint markerY, TextRecord objective)
		{
			var goal = def.Goals.FirstOrDefault(g => g.GoalId == goalId);
			if (goal == null)
			{
				def.Goals.Add(goal = new QuestGoalDefinition
				{
					GoalId = goalId,
					Sequence = 1,
					RequiredCount = 1
				});
			}

			goal.MapId = mapId;
			goal.MarkerX = markerX;
			goal.MarkerY = markerY;
			goal.ObjectiveRefId = objective.Id;
			goal.ObjectiveCategory = objective.Category;
			goal.ObjectiveKey = LdbKey.Decode(objective.Key) ?? new();
		}

		private sealed record TextRecord(int Offset, int Length, string Tag, uint Id, uint Category, string Key, string Flags);
	}
}
