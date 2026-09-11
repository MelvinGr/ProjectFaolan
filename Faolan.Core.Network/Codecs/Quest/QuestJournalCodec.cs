using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Quest
{
	/// <summary>
	///     QuestJournalIIR - the whole J-panel quest list.
	/// </summary>
	/// <remarks>
	///     Wire, from the client's QuestJournalIIR_c::ReadSubClass (FUN_0091a4e0):
	///     <code>
	///     [subop][class][player][u8 0x01]
	///     one QuestJournalArea                     the first area block, inline (FUN_00922c90)
	///     [u32 mergeType]
	///     [ListFrame'd list of QuestJournalArea]   further area blocks (FUN_00798c70) - empty in all 38 captures
	///     [u32 currentQuestId]
	///     [ListFrame'd list of strings]            the current quest's title and sub-line (FUN_004d55b0)
	///     [u32 currentOrdinal]
	///     </code>
	///     <para>
	///         <c>mergeType</c> is the client's own name: Activate hands it to JournalHolder_c::UpdateQuestEntryList
	///         (FUN_00819f80), which switches on it and logs "Invalid MergeType!" on anything past 4. The ops, from that
	///         switch, against the per-character journal holder at char+0x32c: 0 appends the first area and announces each of
	///         its entries; 1 updates the held area matching the first area's id, stamping the incoming area's trailing word
	///         onto every held area (see QuestJournalArea.TrailerFlag); 2 applies the further-areas list; 3 removes the area
	///         matching the first area's id; 4 is a single-entry update within the matching area. Captured journals use only
	///         0, for a one-quest journal's first append, and 1, for every larger whole-journal refresh naming the last
	///         entry.
	///     </para>
	///     <para>
	///         The five trailing fields are not two count-framed lists. <c>mergeType</c> is its own word, the record list is
	///         frame-only with no count word, and the string list after <c>currentQuestId</c> carries the named quest's two
	///         text records. Reading them as a tracked-quest pointer followed by two [frame][count] lists happens to fit a
	///         one-quest journal and mis-splits every larger one. Both captured shapes fall out exactly: (0, empty, 0, empty,
	///         0) for a one-quest journal, and (1, empty, questId, 2 strings, ordinal) naming the last entry for the rest.
	///     </para>
	///     <para>The u32 length prefix belongs to the framing sender, not to this body.</para>
	/// </remarks>
	public static class QuestJournalCodec
	{
		public const uint Opcode = (uint)IirTypes.QuestJournalIIR;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
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

	/// <summary>
	///     The QuestJournalIIR body. <c>MergeType</c> is the holder op the journal asks for: 0 (append) and 1
	///     (update/refresh) are the two captured values, and 2 to 4 exist in the client's switch.
	/// </summary>
	public sealed record QuestJournalIIR(
		uint PlayerHandle,
		QuestJournalArea Area,
		uint MergeType,
		IReadOnlyList<QuestJournalArea> MoreAreas,
		QuestJournalCurrent Current);

	/// <summary>
	///     The journal's trailing block, naming one quest again after the area blocks. In every capture it is the last
	///     entry of the list - the quest just accepted or advanced, the journal being re-sent whole on each change - which
	///     the client presumably scrolls to or highlights.
	/// </summary>
	/// <remarks>
	///     The fields agree in all 38 captures: a MergeType-0 append carries (0, [], 0) and every MergeType-1 refresh
	///     carries (lastQuestId, [title, subLine], entryCount). They are modelled separately rather than collapsed into a
	///     nullable quest so that any combination round-trips, which is what a strict Parse needs.
	/// </remarks>
	public sealed record QuestJournalCurrent(uint QuestId, IReadOnlyList<string> Text, uint Ordinal)
	{
		public static QuestJournalCurrent None { get; } = new QuestJournalCurrent(0, [], 0);
	}
}
