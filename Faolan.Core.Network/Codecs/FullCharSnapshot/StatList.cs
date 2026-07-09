using System.Collections.Generic;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	// A 0x3F1-framed StatList_t as the client reads it (CoreFullPlayerCharacterIIR reader, FUN_0139d450 -> the
	// StatPair_t reader FUN_0139d210 = [u32 statId][u32 value]; docs/re/fullplayer-snapshot-reader.md regions 9-10).
	// The list is framed by a single count word `c` where c % 0x3F1 == 0 && c > 0 and the record count is
	// c / 0x3F1 - 1 (the client asserts "(nSize%1009)==0 && nSize>0"). The stat block is TWO of these back-to-back:
	// StatList#1 = the 17 DISPLAYED stats (Level 0x36 / XP 0x34 / MaxXP 0x15E / HP 0x1B / ...), StatList#2 = the 148
	// full-sheet stats (what the old "repeated run" was). This is the real grammar behind the former opaque 338-value
	// skeleton - so the sheet is authored as (statId, value) pairs from the character, not carried verbatim.
	public sealed class StatList : ICodec<StatList>
	{
		public const uint Frame = 0x3F1;

		public List<(uint Id, uint Value)> Pairs { get; init; } = [];

		// The framing count word for this list: (records + 1) * 0x3F1.
		public uint CountWord => ((uint)Pairs.Count + 1) * Frame;

		// Reads a StatList at the stream's current position. Assumes a valid 0x3F1-framed count (caller has checked it
		// is a stat list, not the empty-list markers).
		public static StatList Read(FaolanStream r)
		{
			var count = r.ReadUInt32();
			var records = count / Frame - 1;
			var list = new StatList();
			for (var i = 0u; i < records; i++)
				list.Pairs.Add((r.ReadUInt32(), r.ReadUInt32()));
			return list;
		}

		public void Write(FaolanStream w)
		{
			w.WriteUInt32(CountWord);
			foreach (var (id, value) in Pairs)
				w.WriteUInt32(id).WriteUInt32(value);
		}

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
