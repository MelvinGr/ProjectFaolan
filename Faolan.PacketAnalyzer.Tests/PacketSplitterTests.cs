using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Network.Packets;
using Xunit;

namespace Faolan.PacketAnalyzer.Tests
{
	public class PacketSplitterTests
	{
		[Fact]
		public void DataReceived_ReassemblesMultiplePackets_WhenSmallFragmentsSpanPacketBoundaries()
		{
			var splitter = new PacketSplitter("test-flow");
			var firstPacketBytes = BuildPacketBytes(0x2001, [0x01, 0x02]);
			var secondPacketBytes = BuildPacketBytes(0x2002, [0x03, 0x04, 0x05]);
			var combinedBytes = firstPacketBytes.Concat(secondPacketBytes).ToArray();
			var receivedPackets = new List<Packet>();
			var receivedTicks = new List<string>();

			splitter.ReceivedPacket = (ticks, packet) =>
			{
				receivedTicks.Add(ticks);
				receivedPackets.Add(packet);
			};

			foreach (var fragment in SplitIntoChunks(combinedBytes, 1, 2, 1, 3, 2, 4, 1, 5, 2, 1, 3))
				splitter.DataReceived(987654321, fragment);

			Assert.Equal(new[] { "987654321_0", "987654321_1" }, receivedTicks);
			Assert.Equal(2, receivedPackets.Count);
			Assert.Equal(firstPacketBytes, receivedPackets[0].Bytes);
			Assert.Equal(secondPacketBytes, receivedPackets[1].Bytes);
		}

		[Fact]
		public void DataReceived_ReassemblesPacket_WhenBytesArriveOneByteAtATime()
		{
			var splitter = new PacketSplitter("test-flow");
			var expectedBytes = BuildPacketBytes(0x2000, [0xDE, 0xAD, 0xBE, 0xEF]);
			var receivedPackets = new List<Packet>();
			var receivedTicks = new List<string>();

			splitter.ReceivedPacket = (ticks, packet) =>
			{
				receivedTicks.Add(ticks);
				receivedPackets.Add(packet);
			};

			foreach (var b in expectedBytes)
				splitter.DataReceived(123456789, [b]);

			var packet = Assert.Single(receivedPackets);
			Assert.Equal("123456789_0", Assert.Single(receivedTicks));
			Assert.Equal(expectedBytes, packet.Bytes);
		}

		private static byte[] BuildPacketBytes(ushort opcode, byte[] body)
		{
			return TestPacketFactory.BuildPacketBytes(opcode, body);
		}

		private static IEnumerable<byte[]> SplitIntoChunks(byte[] bytes, params int[] chunkSizes)
		{
			var offset = 0;

			foreach (var chunkSize in chunkSizes)
			{
				if (offset >= bytes.Length)
					yield break;

				var length = chunkSize <= 0
					? 1
					: Math.Min(chunkSize, bytes.Length - offset);

				var chunk = new byte[length];
				Array.Copy(bytes, offset, chunk, 0, length);
				offset += length;
				yield return chunk;
			}

			while (offset < bytes.Length)
			{
				var chunk = new[] { bytes[offset] };
				offset++;
				yield return chunk;
			}
		}
	}
}
