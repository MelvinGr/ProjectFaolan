using System;

namespace Faolan.Core.Network.Codecs
{
	// The protocol's COUNT-FRAMED LIST word: a u32 == (elementCount + 1) * 0x3F1, written ahead of the elements.
	// The client's readers assert `word % 0x3F1 == 0` and take `count = word / 0x3F1 - 1`, so the word is DERIVED
	// from the count and never a literal - a 0x3F1-invalid word (e.g. a verbatim 0 for an empty list) is exactly the
	// kind of thing the client rejects.
	//
	// This word turns up all over: guild lists/rankings, the AH search grid, tradeskill recipes, quest goal + index
	// regions, the spell book, the shortcut bar, stat lists, the player-spawn scaffold, GameAction lists, and the
	// resurrection spots. It had ELEVEN different names before this type existed (ListFraming, Frame, ListStride,
	// ListCountUnit, Marker, ListMultiplier, ListFrame, listMarker, + raw 0x3F1 / 0x7E2 / decimal 1009 literals),
	// which is precisely why it kept getting re-mistaken for a constant.
	//
	// ⚠️ THE 0x7E2 TRAP. A ONE-element list frames as 2 * 0x3F1 = 0x7E2, and single-element lists are the common
	// case in captures - so 0x7E2 reads as a fixed magic number and gets written down as one. It has happened three
	// times:
	//   - the resurrection-spots body carried "const header 0x7E2" for months (every capture has 1 spot) - it was the
	//     count, and sending 2 spots put a wrong word on the wire (#372);
	//   - the AH search-results `head` was documented as "the query's category root, echoed, NOT a quantity" - it is
	//     the count (an EMPTY grid is a 4-byte body whose word is 0x3F1);
	//   - SendMail's 0x7E2 is still read as a "category constant" off ONE captured mail that has exactly ONE
	//     attachment (#378).
	// So: when a "constant" equals n * 0x3F1, suspect a list count, and be suspicious of any grammar pinned only by
	// single-element captures.
	public static class ListFrame
	{
		// The word for an empty list (count 0). Spelled out because it is easy to write 0 by mistake, which the
		// client rejects.
		public const uint Empty = Unit;

		// The framing multiplier. Every count-framed word is a multiple of it.
		public const uint Unit = 0x3F1;

		// The element count encoded in `word`. Throws if it isn't a valid framing word - the same check the client
		// makes, so a throw here means we (or the peer) built something the client would reject too.
		public static int CountOf(uint word)
		{
			if (!TryCountOf(word, out var count))
				throw new FormatException($"0x{word:X} is not a count-framed list word (not a non-zero multiple of 0x{Unit:X})");

			return count;
		}

		// The word for a list of `count` elements.
		public static uint ForCount(int count)
		{
			ArgumentOutOfRangeException.ThrowIfNegative(count);
			return (uint)(count + 1) * Unit;
		}

		// True if `word` is a valid count-framed list word, yielding its element count. Use when a word's meaning is
		// genuinely in doubt and a non-framing value must not throw.
		public static bool TryCountOf(uint word, out int count)
		{
			count = 0;
			if (word == 0 || word % Unit != 0)
				return false;

			count = (int)(word / Unit) - 1;
			return true;
		}
	}
}
