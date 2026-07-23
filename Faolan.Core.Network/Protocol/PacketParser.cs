using System;
using Faolan.Core;
using Faolan.Core.Crypt;
using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Protocol
{
	// Wire frame -> Rpc. The counterpart to PacketSerializer; the two are inverses, locked by PacketFrameRoundTripTests.
	// The ONE place a Conan frame is decoded (Gitea #406/#407): [u32 length][u32 crc32][u32 headerLen][0x0A sender]
	// [0x12 receiver][u16 opcode][headerData][body]. Parse is lenient (the CRC is not re-checked - the live wire is
	// trusted once framed, and capture-derived frames may carry a placeholder CRC); IsCrcValid is the independent
	// recompute the round-trip test uses.
	public static class PacketParser
	{
		public static Rpc Parse(ReadOnlySpan<byte> frame)
		{
			return Parse(frame.ToArray());
		}

		public static Rpc Parse(byte[] frame)
		{
			using var stream = new FaolanStream(frame);
			stream.ReadUInt32(); // length - recomputed on serialize
			stream.ReadUInt32(); // crc32 - see IsCrcValid

			using var header = new FaolanStream(stream.ReadArrayUInt32Length());
			var sender = ProtoEndpoint.Read(header);
			var receiver = ProtoEndpoint.Read(header);
			var opcode = header.ReadUInt16();
			var headerData = header.ReadRemainingArray();
			var body = stream.ReadRemainingArray();

			return new Rpc(sender, receiver, opcode, headerData, body) { FrameLength = frame.Length };
		}

		// The parser's INDEPENDENT crc recompute: the frame's crc32 field must equal Crc32 over everything after it
		// (Crc32.Calculate skips the leading length+crc). The guardrail PacketFrameRoundTripTests asserts, so a builder
		// that miswrites a single byte fails there rather than silently on the wire.
		public static bool IsCrcValid(byte[] frame)
		{
			using var stream = new FaolanStream(frame);
			stream.ReadUInt32(); // length
			var crc32 = stream.ReadUInt32();
			return crc32 == Crc32.Calculate(stream);
		}
	}
}
