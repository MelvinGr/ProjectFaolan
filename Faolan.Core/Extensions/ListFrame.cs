using System;

namespace Faolan.Core.Extensions
{
	/// <summary>
	///     The protocol's count-framed list word: a u32 equal to <c>(elementCount + 1) * 0x3F1</c>, written ahead of
	///     the elements.
	/// </summary>
	/// <remarks>
	///     The client's readers assert <c>word % 0x3F1 == 0</c> and take <c>count = word / 0x3F1 - 1</c>, so the word
	///     is derived from the count and never a literal - a 0x3F1-invalid word (a verbatim 0 for an empty list, say)
	///     is exactly what the client rejects. The framing turns up all over: guild lists and rankings, the auction
	///     house search grid, tradeskill recipes, quest goal and index regions, the spell book, the shortcut bar, stat
	///     lists, the player-spawn scaffold, GameAction lists, and the resurrection spots.
	///     <para />
	///     A one-element list frames as <c>2 * 0x3F1 = 0x7E2</c>, and single-element lists are the common case in
	///     captures - so 0x7E2 reads as a fixed magic number and is easily written down as one. An empty list frames
	///     as 0x3F1 itself, so a list that happens to be empty in most captures reads as a closing constant: a
	///     "terminator", a "final marker", a "header pad". The player spawn's post-IIR stream carries five at once,
	///     all of which parse happily across ~200 captured spawns and only break on the ~30 where the list has
	///     elements.
	///     <para />
	///     When a "constant" equals <c>n * 0x3F1</c>, assume it is <see cref="ForCount" /> until a capture with a
	///     non-empty list proves otherwise, and distrust any grammar pinned only by single-element captures. Breadth
	///     over the whole corpus is what surfaces these, not a single fixture.
	/// </remarks>
	public static class ListFrame
	{
		/// <summary>
		///     The word for an empty list (count 0). Spelled out because it is easy to write 0 by mistake, which the
		///     client rejects.
		/// </summary>
		public const uint Empty = Unit;

		/// <summary>The framing multiplier. Every count-framed word is a multiple of it.</summary>
		public const uint Unit = 0x3F1;

		/// <summary>
		///     The element count encoded in <paramref name="word" />.
		/// </summary>
		/// <exception cref="FormatException">
		///     <paramref name="word" /> is not a valid framing word. This is the same check the client makes, so a
		///     throw here means we (or the peer) built something the client would reject too.
		/// </exception>
		public static int CountOf(uint word)
		{
			if (!TryCountOf(word, out var count))
				throw new FormatException($"0x{word:X} is not a count-framed list word (not a non-zero multiple of 0x{Unit:X})");

			return count;
		}

		/// <summary>The word for a list of <paramref name="count" /> elements.</summary>
		public static uint ForCount(int count)
		{
			ArgumentOutOfRangeException.ThrowIfNegative(count);
			return (uint)(count + 1) * Unit;
		}

		/// <summary>
		///     True if <paramref name="word" /> is a valid count-framed list word, yielding its element count. Use
		///     when a word's meaning is genuinely in doubt and a non-framing value must not throw.
		/// </summary>
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
