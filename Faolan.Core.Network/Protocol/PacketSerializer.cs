using Faolan.Core.Crypt;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Protocol
{
	// Rpc -> wire frame. The counterpart to PacketParser; the two are inverses, locked by PacketFrameRoundTripTests.
	// This is the ONE place a Conan frame is encoded: [u32 length][u32 crc32][u32 headerLen][0x0A sender][0x12 receiver]
	// [u16 opcode][headerData][body], with length + crc backfilled once the frame is laid out. (Phase 8, Gitea #406:
	// the header encoding used to live on PacketStream, which is now retired; the zero-alloc span / IBufferWriter
	// encoder is a later drop-in behind this same signature.)
	public static class PacketSerializer
	{
		public static byte[] Serialize(in Rpc rpc)
		{
			var sender = rpc.Sender.ToProtobuf();
			var receiver = rpc.Receiver.ToProtobuf();
			var headerData = rpc.HeaderData.Span;
			var body = rpc.Body.Span;

			using var stream = new FaolanStream();
			stream.WriteUInt32(0); // length     - backfilled below
			stream.WriteUInt32(0); // crc32      - backfilled below
			// headerLen = [0x0A][byteLen] + sender + [0x12][byteLen] + receiver + opcode(u16) + headerData
			stream.WriteUInt32((uint)(sizeof(byte) * 2 + sender.Length + sizeof(byte) * 2 + receiver.Length + sizeof(ushort) + headerData.Length));
			stream.WriteByte(0x0A);
			stream.WriteArrayByteLength(sender);
			stream.WriteByte(0x12);
			stream.WriteArrayByteLength(receiver);
			stream.WriteUInt16(rpc.Opcode);
			if (headerData.Length > 0)
				stream.WriteArray(headerData);
			stream.WriteArray(body);

			// Backfill the length (everything after the length field) and the CRC over the whole frame.
			var end = stream.Position;
			stream.Position = 0;
			stream.WriteUInt32(stream.Length - sizeof(uint));
			stream.WriteUInt32(Crc32.Calculate(stream));
			stream.Position = end;
			return stream.ToArray();
		}
	}
}
