using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Faolan.Core.Database.Tables;

namespace Faolan.Importer.Extractors
{
	// Harvests quest journal-content definitions (QuestDefinition + QuestTextRef rows) from captured QuestFullUpdate
	// (0x63268258) and QuestJournal (0x79f8c7a1) packets. Grammar + round-trip proof: Captures/scripts/decode_quest.py;
	// builders: WorldQuest.BuildQuestJournal/BuildQuestFullUpdate (byte-exact golden tests). Construct, don't replay:
	// we keep ids + the deterministic per-(id,category) content keys, never literal text. A quest's full slot set
	// needs both packets - the full-update carries title/summary/objective/headers, the journal carries the list
	// sub-line (category 50303) - so we merge them by quest id.
	public static class QuestDefinitionExtractor
	{
		private const uint FullUpdateSubOpcode = 0x63268258;
		private const uint JournalSubOpcode = 0x79F8C7A1;

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
			}

			// Only keep quests whose full-update was seen (it carries the bulk of the slots + the marker).
			return byQuest.Values.Where(q => q.TextRefs.Any(r => r.Slot == QuestTextSlot.Title)).ToList();
		}

		private static string Attr(string xml, string name)
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
		private static byte[] InnerBody(byte[] bytes)
		{
			if (bytes == null || bytes.Length < 12)
				return null;

			var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4));
			var start = 12 + headerLen;
			return start < bytes.Length
				? bytes[start..]
				: null;
		}

		private static void MergeFullUpdate(byte[] body, Dictionary<uint, QuestDefinition> byQuest)
		{
			// content @16: [00000000][01][u32 questId]...
			var questId = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(21, 4));
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

			// The two 52000 refs are region (smaller id, e.g. 4) then map (the map id). Map id also gates the marker.
			var region = region52000.OrderBy(r => r.Id).FirstOrDefault();
			var map = region52000.OrderByDescending(r => r.Id).FirstOrDefault();
			if (map != null)
			{
				def.MapId = map.Id;
				// Marker block sits right before the objective record: [mapId][markerX][markerY] then 10 zero bytes.
				var mx = objective.Offset - 18;
				if (mx >= 0 && BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(objective.Offset - 22, 4)) == map.Id)
				{
					def.MarkerX = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(objective.Offset - 18, 4));
					def.MarkerY = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(objective.Offset - 14, 4));
				}
			}

			SetRef(def, QuestTextSlot.Title, title);
			SetRef(def, QuestTextSlot.Summary, summary);
			SetRef(def, QuestTextSlot.AreaTitle, areaTitle);
			SetRef(def, QuestTextSlot.RegionName, region);
			SetRef(def, QuestTextSlot.MapName, map);
			SetRef(def, QuestTextSlot.Objective, objective);
			SetRef(def, QuestTextSlot.ObjectiveLocalized, objectiveLocalized);
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

			var count = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(off, 4));
			off += 4;

			for (uint i = 0; i < count && off + 12 <= body.Length; i++)
			{
				var questId = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(off + 4, 4));
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

		// A <remoteformat>/<localized> slot: 00 <u8 len> <ascii xml>. Returns null if no record starts at off.
		private static TextRecord ReadRecord(byte[] b, int off)
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
			return new TextRecord(off, 2 + len, tag, id, cat, key, flags);
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

		private static void SetRef(QuestDefinition def, QuestTextSlot slot, TextRecord rec)
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
			existing.KeyText = rec.Key;
			existing.IsLocalized = rec.Tag == "localized";
			existing.Flags = rec.Flags;
		}

		private sealed record TextRecord(int Offset, int Length, string Tag, uint Id, uint Category, string Key, string Flags);
	}
}
