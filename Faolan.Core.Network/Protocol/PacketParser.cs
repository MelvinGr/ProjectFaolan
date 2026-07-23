using System;
using Faolan.Core.Network.Packets.Conan;

namespace Faolan.Core.Network.Protocol
{
	// Wire frame -> Rpc. The counterpart to PacketSerializer; the two are inverses, locked by PacketFrameRoundTripTests.
	// Currently reuses ConanPacket's header read (the same parse the inbound path already trusts), lifted into the
	// structured Rpc model so both directions can flow one type (Gitea #406, phase 2).
	public static class PacketParser
	{
		public static Rpc Parse(ReadOnlySpan<byte> frame)
		{
			return Parse(frame.ToArray());
		}

		public static Rpc Parse(byte[] frame)
		{
			var packet = new ConanPacket(frame);
			return new Rpc(packet.Sender, packet.Receiver, packet.Opcode, packet.HeaderData, packet.Body.ToArray()) { FrameLength = frame.Length };
		}
	}
}
