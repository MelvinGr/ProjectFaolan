using System;
using System.Collections.Generic;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	/// <summary>
	///     The per-character known list carried inside the FullChar snapshot tails (the spellbook region): a count-framed
	///     list of 12-byte records, one per spell or feat the character knows.
	/// </summary>
	/// <remarks>
	///     Decoded byte-exact from the Faolanx (clean fresh necromancer) login snapshot, whose list is exactly the two
	///     create-spells, and matching the empty list in the beach template.
	///     <para>
	///         Each record is big-endian <c>[u32 id][u32 0xFFFFFFFF][u8 0x00 x3][u8 type]</c>. The 0xFFFFFFFF is the
	///         unbound bar slot; type 2 is a spell and type 5 a feat or ability. A fresh character's list is just its
	///         learned spells (type 2) - the three Normal Attacks do not appear, since Faolanx's list has no type-5
	///         records - so this carries spells only.
	///     </para>
	///     <para>
	///         Composed by <see cref="SubClassPostAccessBlockCodec" />, which carries it as the block's optional spellbook
	///         list. The value is what production builds (<c>KnownSpellLists.FromSpellIds</c>);
	///         <see cref="KnownSpellListScan" /> is what the importer lifts a captured one with.
	///     </para>
	/// </remarks>
	public static class KnownSpellListCodec
	{
		// The "no bound bar slot" word every record carries in its second position.
		public const uint Unbound = 0xFFFFFFFF;

		public const byte FeatType = 5;
		public const byte SpellType = 2;

		// [u32 id][u32 0xFFFFFFFF][u32 type]
		public const int RecordSize = 12;

		// Canonical: the only method here that writes bytes.
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

	/// <summary>The known list: one record per spell or feat the character knows.</summary>
	public sealed record KnownSpellList
	{
		public List<KnownSpellRecord> Records { get; init; } = [];
	}

	public readonly record struct KnownSpellRecord(uint Id, byte Type);
}
