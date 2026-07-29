using System;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// The range the client asserts on every GameAction id, and the stride trap that produced an id outside it.
	public class GameActionTypeIdTests
	{
		// `LEA EAX,[ESI + 0xffff30f7]; CMP EAX,0x11d; JBE ok` - valid iff (id - 0xCF09) <= 0x11D.
		[Fact]
		public void Range_IsWhatTheClientAsserts()
		{
			Assert.Equal(0xCF09u, GameActionTypeId.First);
			Assert.Equal(0xD027u, GameActionTypeId.End);
			Assert.Equal(0x11Du, GameActionTypeId.End - 1 - GameActionTypeId.First);

			Assert.True(GameActionTypeId.IsValid(GameActionTypeId.First));
			Assert.True(GameActionTypeId.IsValid(GameActionTypeId.End - 1));
			Assert.False(GameActionTypeId.IsValid(GameActionTypeId.First - 1));
			Assert.False(GameActionTypeId.IsValid(GameActionTypeId.End));
		}

		// ⚠️ The regression that crashed the client (2026-07-29). The descriptor table is indexed with stride 0x104;
		// reading it with 0x41 puts StartFlymode at index 548 -> 0xD12D instead of index 137 -> 0xCF92. Because
		// 0x104 == 4 * 0x41 the wrong index STILL divides evenly, so "it lands on a descriptor" proves nothing -
		// only the range does.
		[Fact]
		public void TheFourTimesInflatedId_IsRejected()
		{
			const long tableBase = 0x025077C8, startFlymodeDescriptor = 0x025102EC;

			var correct = 0xCF09 + (startFlymodeDescriptor - tableBase) / 0x104;
			var inflated = 0xCF09 + (startFlymodeDescriptor - tableBase) / 0x41;

			Assert.Equal(0xCF92, correct);
			Assert.Equal(0xD12D, inflated);
			Assert.Equal(0, (startFlymodeDescriptor - tableBase) % 0x41); // the vacuous check that fooled us
			Assert.True(GameActionTypeId.IsValid((uint)correct));
			Assert.False(GameActionTypeId.IsValid((uint)inflated));
		}

		// The builder must refuse an out-of-range type rather than hand it to the client, where it is an assert
		// dialog and a disconnect rather than a dropped packet.
		[Fact]
		public void BuildingAnOutOfRangeTypeThrows()
		{
			Assert.Throws<ArgumentOutOfRangeException>(() => GameActionElement.Build(0xD12D, 0, 0, []));
			Assert.Throws<ArgumentOutOfRangeException>(() => GameActionElement.Build(GameActionTypeId.First - 1, 0, 0, []));
			Assert.Throws<ArgumentOutOfRangeException>(() => GameActionElement.Build(GameActionTypeId.End, 0, 0, []));

			// ...and a valid one still builds (the recast override's type).
			Assert.NotEmpty(GameActionElement.Build(0xCF81, 0, 0, [1u, 2u]));
		}
	}
}
