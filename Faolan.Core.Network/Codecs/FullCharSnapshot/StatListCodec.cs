using System;
using System.Collections.Generic;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	/// <summary>
	///     A count-framed <c>StatList_t</c> as the client reads it (the CoreFullPlayerCharacterIIR reader FUN_0139d450
	///     into the <c>StatPair_t</c> reader FUN_0139d210 = <c>[u32 statId][u32 value]</c>;
	///     docs/re/fullplayer-snapshot-reader.md regions 9-10).
	/// </summary>
	/// <remarks>
	///     The list is framed by the ordinary <c>ListFrame</c> count word, and the client asserts the word is a whole
	///     number of frame units and greater than zero. The stat block is two of these back to back: the first is the 17
	///     displayed stats (Level 0x36, XP 0x34, MaxXP 0x15E, HP 0x1B, …) and the second the 148 full-sheet stats, so the
	///     sheet is authored as (statId, value) pairs from the character rather than carried verbatim. Composed by
	///     <see cref="SubClassPostAccessBlockCodec" />, which reads the block as these two lists.
	/// </remarks>
	public static class StatListCodec
	{
		// Canonical: the only method here that writes bytes.
		public static byte[] Build(StatList value)
		{
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static StatList Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(StatListCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static StatList Read(ref SpanReader r)
		{
			var records = r.ReadListCount();
			var list = new StatList();
			for (var i = 0; i < records; i++)
				list.Pairs.Add((r.ReadU32Be(), r.ReadU32Be()));

			return list;
		}

		internal static void Write(FaolanStream s, StatList v)
		{
			ArgumentNullException.ThrowIfNull(v);
			s.WriteUInt32(ListFrame.ForCount(v.Pairs.Count));
			foreach (var (id, value) in v.Pairs)
				s.WriteUInt32(id).WriteUInt32(value);
		}
	}

	/// <summary>One <c>StatList_t</c>: the (statId, value) pairs it frames.</summary>
	public sealed record StatList
	{
		public List<(uint Id, uint Value)> Pairs { get; init; } = [];

		public uint? Get(uint statId)
		{
			foreach (var (id, value) in Pairs)
			{
				if (id == statId)
					return value;
			}

			return null;
		}

		// Sets a stat's value at every occurrence of the id - the character-derived override (Level, HP, XP, …).
		public void Set(uint statId, uint value)
		{
			for (var i = 0; i < Pairs.Count; i++)
			{
				if (Pairs[i].Id == statId)
					Pairs[i] = (statId, value);
			}
		}
	}
}
