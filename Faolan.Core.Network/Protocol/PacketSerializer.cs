using Faolan.Core.Network.Packets;

namespace Faolan.Core.Network.Protocol
{
	// Rpc -> wire frame. The counterpart to PacketParser; the two are inverses, locked by PacketFrameRoundTripTests.
	//
	// This currently reuses the proven PacketStream header encoder, so the output is byte-identical to today's send
	// path BY CONSTRUCTION - the point of phase 2 (Gitea #406) is the Rpc<->bytes boundary the transport will sit on,
	// not a byte-level rewrite. The zero-alloc span / IBufferWriter encoder (reserve length+crc, write, backfill) is a
	// later drop-in behind this same signature (phase 7), where the Pipelines transport makes the allocation matter.
	// Unlike the fluent send pattern, the stream is disposed here.
	public static class PacketSerializer
	{
		public static byte[] Serialize(in Rpc rpc)
		{
			using var packet = new PacketStream();
			packet.WriteHeader(rpc.Sender, rpc.Receiver, rpc.Opcode, rpc.HeaderData.Length > 0
				? rpc.HeaderData.ToArray()
				: null);
			packet.WriteArray(rpc.Body.ToArray());
			packet.WriteLengthHash();
			return packet.ToArray();
		}
	}
}
