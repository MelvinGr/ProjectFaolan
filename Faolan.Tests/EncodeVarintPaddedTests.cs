using System;
using Faolan.GameServer;
using Xunit;

namespace Faolan.Tests
{
	// Locks the overlong-varint padding that lets the world-burst replay re-address ANY captured instance, including
	// maps 1000/11040 whose captured instance (e.g. 0x110099BD) is a 5-byte varint while live character ids are
	// 4-byte. RewriteBurstInstance pads the live id's varint to the captured length so the substitution stays
	// length-preserving; a padded (non-minimal) varint must still decode to the original value.
	public class EncodeVarintPaddedTests
	{
		private static ulong DecodeVarint(byte[] bytes)
		{
			ulong value = 0;
			var shift = 0;
			foreach (var b in bytes)
			{
				value |= (ulong)(b & 0x7F) << shift;
				shift += 7;
				if ((b & 0x80) == 0)
					break;
			}

			return value;
		}

		[Theory]
		[InlineData(0x02000005u, 4)] // in-range live id, natural 4-byte length (no padding)
		[InlineData(0x02000005u, 5)] // same id padded to a 5-byte slot (captured out-of-range instance)
		[InlineData(1u, 5)] // small value padded into a long slot
		[InlineData(0xFFFFFFFFu, 5)] // 32-bit max needs the full 5 bytes
		public void Padded_HasRequestedLength_AndRoundTrips(uint value, int byteLength)
		{
			var bytes = GameServerListener.EncodeVarintPadded(value, byteLength);

			Assert.NotNull(bytes);
			Assert.Equal(byteLength, bytes!.Length);
			// every byte except the last carries the continuation bit; the last must not
			for (var i = 0; i < bytes.Length - 1; i++)
				Assert.True((bytes[i] & 0x80) != 0, $"byte {i} should continue");
			Assert.True((bytes[^1] & 0x80) == 0, "last byte must terminate");
			Assert.Equal(value, DecodeVarint(bytes));
		}

		[Fact]
		public void Padded_ReturnsNull_WhenValueDoesNotFit()
		{
			// 0xFFFFFFFF needs 5 varint bytes; it can't be squeezed into a 4-byte captured slot.
			Assert.Null(GameServerListener.EncodeVarintPadded(0xFFFFFFFFu, 4));
		}
	}
}
