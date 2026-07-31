using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Quest
{
	// QuestJournalIIR (0x5A2FE45E) - the whole J-panel quest list.
	//
	// Wire (client `QuestJournalIIR_c::ReadSubClass` = `FUN_0091a4e0`):
	//   [subop][class][player][u8 0x01]
	//   <QuestJournalArea>                       the first area block, inline (FUN_00922c90)
	//   [u32 currentFlag]
	//   [ListFrame'd list of <QuestJournalArea>] further area blocks (FUN_00798c70) - EMPTY in all 38 captures
	//   [u32 currentQuestId]
	//   [ListFrame'd list of strings]            the current quest's title + sub-line (FUN_004d55b0)
	//   [u32 currentOrdinal]
	//
	// ⚠️ The five trailing fields were read as a "tracked-quest pointer" - `[u32 trackedQuestId][list A][list B]`,
	// each list `[frame][count]` - which is what rejected 31 of the 38 captured journals (#544): in a journal with
	// more than one quest, list A's "count" slot holds a quest id and list B's frame word holds 0xBD3. The client
	// reader shows why: they are not two count-framed lists. `currentFlag` is its own word, the record list is
	// frame-ONLY (no count word), and the string list after `currentQuestId` carries the named quest's two text
	// records. Both captured shapes fall out exactly - (0, empty, 0, empty, 0) for a one-quest journal, and
	// (1, empty, questId, 2 strings, ordinal) naming the LAST entry for every larger one.
	//
	// The u32 length prefix belongs to the SENDER (#530 rule 2).
	public static class QuestJournalCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.QuestJournalIIR;

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

			var flag = r.ReadU32Be();

			var moreCount = r.ReadListCount();
			var more = new List<QuestJournalArea>(moreCount);
			for (var i = 0; i < moreCount; i++)
				more.Add(QuestJournalAreaCodec.Read(ref r));

			var currentQuestId = r.ReadU32Be();
			var currentText = QuestJournalAreaCodec.ReadStringList(ref r);
			var currentOrdinal = r.ReadU32Be();

			return new QuestJournalIIR(handle, area, more, new QuestJournalCurrent(flag, currentQuestId, currentText, currentOrdinal));
		}

		internal static void Write(FaolanStream s, QuestJournalIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.PlayerHandle).WriteByte((byte)0x01);
			QuestJournalAreaCodec.Write(s, v.Area);

			s.WriteUInt32(v.Current.Flag);
			s.WriteUInt32(ListFrame.ForCount(v.MoreAreas.Count));
			foreach (var area in v.MoreAreas)
				QuestJournalAreaCodec.Write(s, area);

			s.WriteUInt32(v.Current.QuestId);
			QuestJournalAreaCodec.WriteStringList(s, v.Current.Text);
			s.WriteUInt32(v.Current.Ordinal);
		}
	}

	// QuestJournalIIR (0x5A2FE45E).
	public sealed record QuestJournalIIR(
		uint PlayerHandle,
		QuestJournalArea Area,
		IReadOnlyList<QuestJournalArea> MoreAreas,
		QuestJournalCurrent Current);

	// The journal's trailing block, which names ONE quest again after the area blocks - in every capture the LAST
	// entry of the list, i.e. the quest that was just accepted or advanced (the journal is re-sent whole on each
	// change). The client presumably scrolls/highlights it.
	//
	// The four fields agree in all 38 captures: a one-quest journal carries (0, 0, [], 0) and every larger one carries
	// (1, lastQuestId, [title, subLine], entryCount). They are modelled separately rather than collapsed to a nullable
	// quest so that any combination round-trips, which is what a strict Parse needs.
	public sealed record QuestJournalCurrent(uint Flag, uint QuestId, IReadOnlyList<string> Text, uint Ordinal)
	{
		public static QuestJournalCurrent None { get; } = new(0, 0, [], 0);
	}
}
