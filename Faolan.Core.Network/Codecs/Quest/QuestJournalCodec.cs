using System;
using System.Collections.Generic;
using System.Text;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Quest
{
	// QuestJournalIIR (0x5A2FE45E) - the whole J-panel quest list.
	//   [subop][class][player][u8 0x01][u32 areaId]
	//   [string areaTitle][string mapName][string regionName]      the area header (cat 50300 + two cat 52000)
	//   "RRL3"                                                     4 raw Latin1 bytes, the record-list magic
	//   [u32 0][u32 count]
	//   per entry: [u32 ordinal(1-based)][u32 questId][u32 (n + 1) * 0x3F1][string title][string subLine]
	//   [tracked-quest pointer]
	//
	// ⚠️ The per-entry word before the title is a ListFrame COUNT, not a "ChapterTag": every captured Tortage entry
	// carries 0xBD3, which is ForCount(2) - the same shape the @609 objective region's lead word has. It is carried
	// per entry rather than written as a literal (#530: there is no such thing as a 0x3F1 constant). WHICH list it
	// frames is not decoded - no entries follow it here - so it is carried, not interpreted.
	//
	// The u32 length prefix belongs to the SENDER (#530 rule 2).
	public static class QuestJournalCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.QuestJournalIIR;

		private const string RecordListMagic = "RRL3";

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
			var areaId = r.ReadU32Be();

			var areaTitle = r.ReadStringU16Length();
			var mapName = r.ReadStringU16Length();
			var regionName = r.ReadStringU16Length();

			var at = r.Position;
			var magic = Encoding.Latin1.GetString(r.ReadBytes(RecordListMagic.Length));
			if (magic != RecordListMagic)
				throw CodecParseException.Unexpected(nameof(QuestJournalCodec), at, RecordListMagic, magic);

			r.ExpectU32Be(0, "reserved word after the record-list magic");

			var count = r.ReadU32Be();
			var entries = new List<QuestJournalEntry>((int)count);
			for (var i = 0; i < count; i++)
			{
				r.ExpectU32Be((uint)(i + 1), "1-based journal ordinal");
				var questId = r.ReadU32Be();
				var chapterCount = r.ReadListCount();
				entries.Add(new QuestJournalEntry(questId, r.ReadStringU16Length(), r.ReadStringU16Length())
				{
					ChapterCount = chapterCount
				});
			}

			return new QuestJournalIIR(handle, areaId, areaTitle, mapName, regionName, entries, TrackedQuestPointerCodec.Read(ref r));
		}

		internal static void Write(FaolanStream s, QuestJournalIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.PlayerHandle).WriteByte((byte)0x01).WriteUInt32(v.AreaId);
			s.WriteString(v.AreaTitle);
			s.WriteString(v.MapName);
			s.WriteString(v.RegionName);
			s.WriteArray(Encoding.Latin1.GetBytes(RecordListMagic));
			s.WriteUInt32(0u);
			s.WriteUInt32(v.Entries.Count);

			for (var i = 0; i < v.Entries.Count; i++)
			{
				var e = v.Entries[i];
				s.WriteUInt32(i + 1); // 1-based ordinal
				s.WriteUInt32(e.QuestId);
				s.WriteUInt32(ListFrame.ForCount(e.ChapterCount));
				s.WriteString(e.Title);
				s.WriteString(e.SubLine);
			}

			TrackedQuestPointerCodec.Write(s, v.Tracked);
		}
	}

	// QuestJournalIIR (0x5A2FE45E).
	public sealed record QuestJournalIIR(
		uint PlayerHandle,
		uint AreaId,
		string AreaTitle,
		string MapName,
		string RegionName,
		IReadOnlyList<QuestJournalEntry> Entries,
		TrackedQuestPointer Tracked);

	// One line in the journal list: the quest id plus its title (cat 50301) and list sub-line (cat 50303).
	public sealed record QuestJournalEntry(uint QuestId, string Title, string SubLine)
	{
		// The ListFrame count that precedes the title. 2 on every captured Tortage entry (the word 0xBD3).
		public int ChapterCount { get; init; } = 2;
	}
}
