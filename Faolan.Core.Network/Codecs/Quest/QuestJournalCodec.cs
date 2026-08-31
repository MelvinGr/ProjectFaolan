using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Quest
{
	// QuestJournalIIR (0x79F8C7A1) - the whole J-panel quest list. (An earlier header here said 0x5A2FE45E; the
	// codec's Opcode const was always IirTypes.QuestJournalIIR, only the comment's hex was wrong.)
	//
	// Wire (client `QuestJournalIIR_c::ReadSubClass` = `FUN_0091a4e0`):
	//   [subop][class][player][u8 0x01]
	//   <QuestJournalArea>                       the first area block, inline (FUN_00922c90)
	//   [u32 mergeType]
	//   [ListFrame'd list of <QuestJournalArea>] further area blocks (FUN_00798c70) - EMPTY in all 38 captures
	//   [u32 currentQuestId]
	//   [ListFrame'd list of strings]            the current quest's title + sub-line (FUN_004d55b0)
	//   [u32 currentOrdinal]
	//
	// ⭐ `mergeType` is the CLIENT'S OWN name (#464, 2026-08-31): Activate hands it to
	// `JournalHolder_c::UpdateQuestEntryList()` (`FUN_00819f80`), which switches on it and logs
	// "Invalid MergeType!" on anything past 4. The ops, from the switch (holder = the per-character journal at
	// char+0x32c): 0 = APPEND the first area and announce each of its entries; 1 = UPDATE the held area matching
	// the first area's id (and stamp the incoming area's trailing word onto EVERY held area - see
	// QuestJournalAreaCodec.Unknown); 2 = apply the FURTHER-areas list; 3 = REMOVE the area matching the first
	// area's id; 4 = a single-entry update within the matching area. Captured journals use exactly 0 (a one-quest
	// journal, the first append) and 1 (every larger one, the whole-journal refresh naming the last entry).
	//
	// ⚠️ The five trailing fields were read as a "tracked-quest pointer" - `[u32 trackedQuestId][list A][list B]`,
	// each list `[frame][count]` - which is what rejected 31 of the 38 captured journals (#544): in a journal with
	// more than one quest, list A's "count" slot holds a quest id and list B's frame word holds 0xBD3. The client
	// reader shows why: they are not two count-framed lists. `mergeType` is its own word, the record list is
	// frame-ONLY (no count word), and the string list after `currentQuestId` carries the named quest's two text
	// records. Both captured shapes fall out exactly - (0, empty, 0, empty, 0) for a one-quest journal, and
	// (1, empty, questId, 2 strings, ordinal) naming the LAST entry for every larger one.
	//
	// The u32 length prefix belongs to the SENDER (#530 rule 2).
	public static class QuestJournalCodec
	{
		public const uint Opcode = (uint)IirTypes.QuestJournalIIR;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(QuestJournalIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static QuestJournalIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(QuestJournalCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static QuestJournalIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "QuestJournalIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			r.ExpectU8(0x01, "record marker");

			var area = QuestJournalAreaCodec.Read(ref r);

			var mergeType = r.ReadU32Be();

			var moreCount = r.ReadListCount();
			var more = new List<QuestJournalArea>(moreCount);
			for (var i = 0; i < moreCount; i++)
				more.Add(QuestJournalAreaCodec.Read(ref r));

			var currentQuestId = r.ReadU32Be();
			var currentText = QuestJournalAreaCodec.ReadStringList(ref r);
			var currentOrdinal = r.ReadU32Be();

			return new QuestJournalIIR(handle, area, mergeType, more, new QuestJournalCurrent(currentQuestId, currentText, currentOrdinal));
		}

		internal static void Write(FaolanStream s, QuestJournalIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.PlayerHandle).WriteByte((byte)0x01);
			QuestJournalAreaCodec.Write(s, v.Area);

			s.WriteUInt32(v.MergeType);
			s.WriteUInt32(ListFrame.ForCount(v.MoreAreas.Count));
			foreach (var area in v.MoreAreas)
				QuestJournalAreaCodec.Write(s, area);

			s.WriteUInt32(v.Current.QuestId);
			QuestJournalAreaCodec.WriteStringList(s, v.Current.Text);
			s.WriteUInt32(v.Current.Ordinal);
		}
	}

	// QuestJournalIIR (0x79F8C7A1). MergeType is the holder op the journal asks for - see the codec header; 0
	// (append) and 1 (update/refresh) are the two captured values, and 2..4 exist in the client's switch.
	public sealed record QuestJournalIIR(
		uint PlayerHandle,
		QuestJournalArea Area,
		uint MergeType,
		IReadOnlyList<QuestJournalArea> MoreAreas,
		QuestJournalCurrent Current);

	// The journal's trailing block, which names ONE quest again after the area blocks - in every capture the LAST
	// entry of the list, i.e. the quest that was just accepted or advanced (the journal is re-sent whole on each
	// change). The client presumably scrolls/highlights it.
	//
	// The fields agree in all 38 captures: a MergeType-0 (append) journal carries (0, [], 0) and every MergeType-1
	// refresh carries (lastQuestId, [title, subLine], entryCount). They are modelled separately rather than collapsed
	// to a nullable quest so that any combination round-trips, which is what a strict Parse needs. (The u32 that used
	// to lead this record as `Flag` is the top-level MergeType - the client reads it OUTSIDE the current-quest group.)
	public sealed record QuestJournalCurrent(uint QuestId, IReadOnlyList<string> Text, uint Ordinal)
	{
		public static QuestJournalCurrent None { get; } = new QuestJournalCurrent(0, [], 0);
	}
}
