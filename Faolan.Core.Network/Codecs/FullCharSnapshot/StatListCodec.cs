using System;
using System.Collections.Generic;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	// A 0x3F1-framed StatList_t as the client reads it (CoreFullPlayerCharacterIIR reader, FUN_0139d450 -> the
	// StatPair_t reader FUN_0139d210 = [u32 statId][u32 value]; docs/re/fullplayer-snapshot-reader.md regions 9-10).
	// The list is framed by the ordinary ListFrame count word and the record count is `word / 0x3F1 - 1` (the client
	// asserts "(nSize%1009)==0 && nSize>0"). The stat block is TWO of these back-to-back: StatList#1 = the 17
	// DISPLAYED stats (Level 0x36 / XP 0x34 / MaxXP 0x15E / HP 0x1B / ...), StatList#2 = the 148 full-sheet stats
	// (what the old "repeated run" was). This is the real grammar behind the former opaque 338-value skeleton - so
	// the sheet is authored as (statId, value) pairs from the character, not carried verbatim.
	//
	// Composed by SubClassPostAccessBlockCodec, which reads the block as these two lists (#543). It used to carry the
	// block as a FLAT u32 view with a second codec walking the same bytes to say where the stride-8 tables sat, while
	// SubClassStatBlock laid the lists out as words - three spellings of one layout.
	public static class StatListCodec
	{
		// CANONICAL - the only method here that writes bytes.
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

	// One StatList_t: the (statId, value) pairs it frames.
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

		// Sets a stat's value (every occurrence of the id) - the character-derived override (Level/HP/XP/...).
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
