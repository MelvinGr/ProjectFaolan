using System;
using System.Collections.Generic;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Quest
{
	// ONE AREA's block of the J-panel journal - the area header plus its quest lines. Composed by QuestJournalCodec,
	// which carries the first block inline and then a (so far always empty) LIST of further ones; never sent alone.
	//
	// Wire (client `FUN_00922c90`, the element reader `FUN_00798c70`'s list is built from):
	//   [u32 areaId][string areaTitle][string mapName][string regionName]
	//   [u32 acgHash][u32 unknown][u32 count]
	//   count x [u32 ordinal][u32 questId][ListFrame'd list of strings]
	//
	// ⚠️ `AcgHash` is NOT a magic. `FUN_005d7190` reads it as the client's **ACGHash** type: a u32 whose four bytes
	// must each be `[0-9A-Z_]`, and which logs `Read invalid hash "..." from stream` on anything else. It is a per-AREA
	// token - area 1054 is "RRL3", 241 is "W6QT", 1834 is "3734", 1772 is "B0DN", stable across every character and
	// capture - so pinning it to "RRL3" (the value the first decoded capture happened to carry) rejected 31 of the 38
	// captured journals (#544). The same token is what QuestDefinition.IndexMagic stores for the quest index.
	public static class QuestJournalAreaCodec
	{
		// The entry's text list: the title (category 50301) and the J-panel sub-line (category 50303). Two on every
		// captured entry, which is why the frame word 0xBD3 read as a "ChapterTag" until the client showed it framing
		// THIS list.
		private const int EntryTextStrings = 2;

		// CANONICAL - the only method here that writes bytes.
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
				Unknown = unknown
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
			s.WriteUInt32(v.Unknown);
			s.WriteUInt32(v.Entries.Count);

			for (var i = 0; i < v.Entries.Count; i++)
			{
				var e = v.Entries[i];
				// The ordinal is 1-based and sequential in every captured journal, but it is a FIELD the client reads
				// back, not a loop index - so it round-trips from the value rather than being regenerated here.
				s.WriteUInt32(e.Ordinal == 0 ? (uint)(i + 1) : e.Ordinal);
				s.WriteUInt32(e.QuestId);
				WriteStringList(s, [e.Title, e.SubLine]);
			}
		}

		// A list of strings framed the client's way (`FUN_004d55b0`): a SINGLE `(count + 1) * 0x3F1` word, then the
		// strings. No separate count word - unlike the record lists, which carry both.
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

	// One area's block of the journal: the shared header plus that area's quest lines.
	public sealed record QuestJournalArea(
		uint QuestLineId,
		string QuestLineTitle, // <remoteformat category=50300>
		string MapName, // <remoteformat category=52000>
		string RegionName, // <remoteformat category=52000>
		IReadOnlyList<QuestJournalEntry> Entries)
	{
		// The area's ACGHash token - see the codec note. Carried per area rather than written as a literal.
		public uint AcgHash { get; init; }

		// The word between the hash and the entry count. 0 on all 38 captured journals; carried rather than asserted.
		public uint Unknown { get; init; }
	}

	// One line in the journal list: the quest id plus its title (cat 50301) and list sub-line (cat 50303).
	public sealed record QuestJournalEntry(uint QuestId, string Title, string SubLine)
	{
		// The entry's 1-based position in its area block. 0 means "let the writer number it" - the factory path builds
		// entries in order and does not care, while a parsed entry round-trips whatever the wire said.
		public uint Ordinal { get; init; }
	}
}
