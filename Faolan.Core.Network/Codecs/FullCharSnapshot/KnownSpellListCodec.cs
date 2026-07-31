using System;
using System.Collections.Generic;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	// The per-character KNOWN list carried inside the 0x737A6DF9 FullChar snapshot tails (the spellbook region): a
	// 0x3F1-framed list of 12-byte records, one per spell/feat the character knows. Decoded byte-exact from the
	// Faolanx (clean fresh necromancer) login snapshot - its list is exactly the 2 create-spells - and matches the
	// empty list in the beach template (count 0x3F1, 0 records).
	//
	// Record (big-endian): [u32 id][u32 0xFFFFFFFF][u8 0x00 x3][u8 type]. The 0xFFFFFFFF is the (unbound) bar slot;
	// type 2 = spell, type 5 = feat/ability. A fresh character's list is just its learned spells (type 2) - the 3
	// Normal Attacks do NOT appear here (Faolanx's list has no type-5 records), so this carries spells only.
	//
	// Composed by SubClassPostAccessBlockCodec (#543), which carries it as the block's optional spellbook list - it
	// used to be laid out inline as words at the block tail, because it sat INSIDE the flat u32 view the block was.
	// The value is what production builds (KnownSpellLists.FromSpellIds); KnownSpellListScan is what the importer
	// lifts a captured one with.
	public static class KnownSpellListCodec
	{
		// The "no bound bar slot" word every record carries in its second position.
		public const uint Unbound = 0xFFFFFFFF;

		public const byte FeatType = 5;
		public const byte SpellType = 2;

		// [u32 id][u32 0xFFFFFFFF][u32 type]
		public const int RecordSize = 12;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(KnownSpellList value)
		{
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static KnownSpellList Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(KnownSpellListCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static KnownSpellList Read(ref SpanReader r)
		{
			var count = r.ReadListCount();
			var records = new List<KnownSpellRecord>(count);
			for (var i = 0; i < count; i++)
			{
				var id = r.ReadU32Be();
				r.ExpectU32Be(Unbound, $"known-list record {i} bar slot");
				records.Add(new KnownSpellRecord(id, (byte)r.ReadU32Be()));
			}

			return new KnownSpellList
			{
				Records = records
			};
		}

		internal static void Write(FaolanStream s, KnownSpellList v)
		{
			ArgumentNullException.ThrowIfNull(v);
			s.WriteUInt32(ListFrame.ForCount(v.Records.Count));
			foreach (var record in v.Records)
			{
				s.WriteUInt32(record.Id);
				s.WriteUInt32(Unbound);
				s.WriteUInt32(record.Type); // big-endian => 00 00 00 <type>
			}
		}
	}

	// The known list: one record per spell/feat the character knows.
	public sealed record KnownSpellList
	{
		public List<KnownSpellRecord> Records { get; init; } = [];
	}

	public readonly record struct KnownSpellRecord(uint Id, byte Type);
}
