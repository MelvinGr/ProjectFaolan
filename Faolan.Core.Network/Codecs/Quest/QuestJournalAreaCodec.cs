using System;
using System.Collections.Generic;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Quest
{
	/// <summary>
	///     One area's block of the J-panel journal: the area header plus its quest lines. Composed by
	///     <see cref="QuestJournalCodec" />, which carries the first block inline and then a list of further ones, so it is
	///     never sent alone.
	/// </summary>
	/// <remarks>
	///     Wire, from the client's FUN_00922c90 (the element the list reader FUN_00798c70 builds from):
	///     <code>
	///     [u32 areaId][string areaTitle][string mapName][string regionName]
	///     [u32 acgHash][u32 trailerFlag][u32 count]
	///     count x [u32 ordinal][u32 questId][ListFrame'd list of strings]
	///     </code>
	///     <para>
	///         <c>AcgHash</c> is a per-area token, not a magic constant: FUN_005d7190 reads it as the client's ACGHash type,
	///         a u32 whose four bytes must each be [0-9A-Z_], logging <c>Read invalid hash "..." from stream</c> on anything
	///         else. Area 1054 is "RRL3", 241 "W6QT", 1834 "3734", 1772 "B0DN", stable across every character and capture, so
	///         pinning it to one value rejects most captured journals. The same token is what QuestDefinition.IndexMagic
	///         stores for the quest index.
	///     </para>
	/// </remarks>
	public static class QuestJournalAreaCodec
	{
		/// <summary>
		///     The entry's text list: the title (category 50301) and the J-panel sub-line (category 50303). Two on every
		///     captured entry, so the frame word ahead of them is constant in the corpus and reads as a tag until the client's
		///     reader shows it framing this list.
		/// </summary>
		private const int EntryTextStrings = 2;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
		public static byte[] Build(QuestJournalArea value)
		{
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static QuestJournalArea Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(QuestJournalAreaCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static QuestJournalArea Read(ref SpanReader r)
		{
			var areaId = r.ReadU32Be();
			var areaTitle = r.ReadStringU16Length();
			var mapName = r.ReadStringU16Length();
			var regionName = r.ReadStringU16Length();

			var acgHash = r.ReadU32Be();
			var unknown = r.ReadU32Be();

			var count = r.ReadU32Be();
			var entries = new List<QuestJournalEntry>((int)count);
			for (var i = 0; i < count; i++)
			{
				var ordinal = r.ReadU32Be();
				var questId = r.ReadU32Be();
				var at = r.Position;
				var text = ReadStringList(ref r);
				if (text.Count != EntryTextStrings)
					throw CodecParseException.Unexpected(nameof(QuestJournalAreaCodec), at, $"an entry text list of {EntryTextStrings} (title + sub-line)", $"{text.Count}");

				entries.Add(new QuestJournalEntry(questId, text[0], text[1])
				{
					Ordinal = ordinal
				});
			}

			return new QuestJournalArea(areaId, areaTitle, mapName, regionName, entries)
			{
				AcgHash = acgHash,
				TrailerFlag = unknown
			};
		}

		internal static void Write(FaolanStream s, QuestJournalArea v)
		{
			ArgumentNullException.ThrowIfNull(v);
			s.WriteUInt32(v.QuestLineId);
			s.WriteString(v.QuestLineTitle);
			s.WriteString(v.MapName);
			s.WriteString(v.RegionName);
			s.WriteUInt32(v.AcgHash);
			s.WriteUInt32(v.TrailerFlag);
			s.WriteUInt32(v.Entries.Count);

			for (var i = 0; i < v.Entries.Count; i++)
			{
				var e = v.Entries[i];
				// The ordinal is 1-based and sequential in every captured journal, but it is a field the client reads
				// back rather than a loop index, so it round-trips from the value instead of being regenerated here.
				s.WriteUInt32(e.Ordinal == 0 ? (uint)(i + 1) : e.Ordinal);
				s.WriteUInt32(e.QuestId);
				WriteStringList(s, [e.Title, e.SubLine]);
			}
		}

		/// <summary>
		///     A list of strings framed the client's way (FUN_004d55b0): a single <see cref="ListFrame" /> word, then the
		///     strings. There is no separate count word, unlike the record lists, which carry both.
		/// </summary>
		internal static List<string> ReadStringList(ref SpanReader r)
		{
			var count = r.ReadListCount();
			var list = new List<string>(count);
			for (var i = 0; i < count; i++)
				list.Add(r.ReadStringU16Length());

			return list;
		}

		internal static void WriteStringList(FaolanStream s, IReadOnlyList<string> strings)
		{
			s.WriteUInt32(ListFrame.ForCount(strings.Count));
			foreach (var text in strings)
				s.WriteString(text);
		}
	}

	/// <summary>One area's block of the journal: the shared header plus that area's quest lines.</summary>
	public sealed record QuestJournalArea(
		uint QuestLineId,
		string QuestLineTitle, // <remoteformat category=50300>
		string MapName, // <remoteformat category=52000>
		string RegionName, // <remoteformat category=52000>
		IReadOnlyList<QuestJournalEntry> Entries)
	{
		/// <summary>The area's ACGHash token, carried per area rather than written as a literal.</summary>
		public uint AcgHash { get; init; }

		/// <summary>
		///     The line's flag - QuestLine.TrailerFlag, the same word the quest index block carries as TrailerC (MainQuest_c
		///     +0x50) and the completed journal's line record carries after its 4CC.
		/// </summary>
		/// <remarks>
		///     This block is that record: FUN_00922c90 is one reader with two callers - the FullChar completed journal,
		///     through the list reader FUN_00798c70, and QuestJournalIIR_c::ReadSubClass (FUN_0091a4e0, the first area at
		///     IIR+0x2c and the further-area list through the same FUN_00798c70 at +0xa0) - and it stores the word at +0x6c
		///     of the 0x70-byte record either way. It is 0 on all 49 captured journal area blocks because every one is on a
		///     line whose flag is 0 (241, 386, 1054, 1772, 1834), which is what a per-line flag predicts; the six flagged
		///     lines are raid, world-boss, seasonal and Onslaught content no captured journal reaches.
		///     <para>
		///         Nothing in the client reads it. A MergeType-1 journal stamps the incoming value onto every held record
		///         (JournalHolder_c::UpdateQuestEntryList, before the area-id match), and that is the only code that touches
		///         the slot after the reader: the journal window band (243 functions), the QJ node and tracker band (604), the
		///         holder's per-record helpers, the completed-area panel and the completed-tab tree builder all read the
		///         record's strings, hash and quest list and skip +0x6c, and MainQuest_c::WriteToStream - the one serializer
		///         of the +0x50 twin - has no caller and sits in no vtable. It is carried so a line reproduces its captured
		///         block.
		///     </para>
		/// </remarks>
		public uint TrailerFlag { get; init; }
	}

	/// <summary>
	///     One line in the journal list: the quest id plus its title (category 50301) and list sub-line (category 50303).
	/// </summary>
	public sealed record QuestJournalEntry(uint QuestId, string Title, string SubLine)
	{
		/// <summary>
		///     The entry's 1-based position in its area block. Zero means "let the writer number it": the factory path builds
		///     entries in order and does not care, while a parsed entry round-trips whatever the wire said.
		/// </summary>
		public uint Ordinal { get; init; }
	}
}
