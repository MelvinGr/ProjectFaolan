using System;
using Faolan.Core.Network.Codecs;
using Xunit;

namespace Faolan.Tests
{
	// The protocol's count-framed list word: (count + 1) * 0x3F1. Pinned here because this word has been mistaken for
	// a magic constant three separate times (see ListFrame's own comment) - every one of them a case where the only
	// captured lists had exactly ONE element, which frames as 2 * 0x3F1 = 0x7E2 and reads like a fixed number.
	public class ListFrameTests
	{
		[Theory, InlineData(0u), InlineData(1u), InlineData(0x3F0u), InlineData(0x7E1u), InlineData(0xC7A0u)]
		// zero is NOT an empty list - it is invalid framing
			// the resurrection-spot type word: a real constant that is NOT count-framed
		public void CountOf_ThrowsOnAWordThatIsNotCountFramed(uint word)
		{
			Assert.False(ListFrame.TryCountOf(word, out _));
			Assert.Throws<FormatException>(() => ListFrame.CountOf(word));
		}

		[Fact]
		public void Empty_IsTheZeroCountWord_NotZero()
		{
			// Writing a bare 0 for an empty list is the mistake this constant exists to prevent: the client's readers
			// assert word % 0x3F1 == 0 and would reject it.
			Assert.Equal(ListFrame.Empty, ListFrame.ForCount(0));
			Assert.Equal(ListFrame.Unit, ListFrame.Empty);
			Assert.NotEqual(0u, ListFrame.Empty);
		}

		// The values that actually appear in the captures, and what each really meant where it was found.
		[Theory, InlineData(0, 0x3F1u), InlineData(1, 0x7E2u), InlineData(2, 0xBD3u), InlineData(3, 0xFC4u), InlineData(7, 0x1F88u), InlineData(78, 0x1375Fu)]
		// an EMPTY list - the AH's empty search grid is a 4-byte body carrying just this
		// ⚠️ THE TRAP: the "constant 0x7E2" of the resurrection-spots body, mail, quest index
		// the 2-spot discovery re-send (#372)
			// a 78-row auction-house search grid
		public void ForCount_MatchesTheWordsSeenInCaptures(int count, uint expected)
		{
			Assert.Equal(expected, ListFrame.ForCount(count));
			Assert.Equal(count, ListFrame.CountOf(expected)); // and round-trips
		}

		[Fact]
		public void ForCount_RejectsNegative()
		{
			Assert.Throws<ArgumentOutOfRangeException>(() => ListFrame.ForCount(-1));
		}
	}
}
