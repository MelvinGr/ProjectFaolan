using System;
using System.Runtime.InteropServices;
using Faolan.Core.Crypt;
using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Protocol
{
	// The structured form of a Conan RPC frame - what a handler builds and hands to the transport, and what the parser
	// yields for an inbound frame - instead of a raw byte[] that has to be re-parsed to be logged or inspected.
	// Serializing this to the wire ([u32 length][u32 crc32][u32 headerLen][sender][receiver][u16 opcode][headerData][body])
	// and parsing it back are inverses (the contract PacketFrameRoundTripTests locks). Introduced for the
	// structured-packet-pipeline epic (Gitea #406).
	//
	// Sender = RpcHeader.caller_id; Receiver = the addressed object - its Type is the interface hash, which names the
	// opcode against it (see ProtocolCatalog). HeaderData is the interface-specific header tail (e.g. the PlayerInterface
	// 32-bit method id, or a keepalive's 1-byte flag); Body is everything after the header. (A collection-expression
	// header literal must be cast, e.g. (byte[])[0x0D, ...], since [..] can't target ReadOnlyMemory<byte>.)
	public readonly record struct Rpc(ProtoEndpoint Sender, ProtoEndpoint Receiver, ushort Opcode, ReadOnlyMemory<byte> HeaderData, ReadOnlyMemory<byte> Body)
	{
		// The total wire frame length. Stamped by whoever turns the message into (or out of) bytes: Parse
		// on the receive path, and the transport on the send path once the serializer has determined the size
		// (`rpc with { FrameLength = … }`). 0 on a freshly built Rpc, whose length isn't knowable yet. The packet trace
		// reports it as packetLength in BOTH directions, so it doesn't have to be threaded alongside the message.
		public int FrameLength { get; init; }

		// Body-building overloads: build the body inline with the fluent FaolanStream writers, e.g.
		//   new Rpc(sender, receiver, opcode, b => b.WriteUInt32(x).WriteArrayUInt32Length(blob))
		// instead of newing + disposing a stream at every call site. The scratch stream is created and DISPOSED here
		// (unlike the old fluent PacketStream send pattern). The Action<FaolanStream> is distinct from the primary
		// ctor's ReadOnlyMemory<byte> Body, so there is no overload ambiguity.
		public Rpc(ProtoEndpoint sender, ProtoEndpoint receiver, ushort opcode, ReadOnlyMemory<byte> headerData, Action<FaolanStream> body) : this(sender, receiver, opcode, headerData,
			BuildBody(body))
		{
		}

		public Rpc(ProtoEndpoint sender, ProtoEndpoint receiver, ushort opcode, Action<FaolanStream> body) : this(sender, receiver, opcode, default, BuildBody(body))
		{
		}

		// Enum-opcode overloads so call sites pass GameInterfaceOpcodes.Foo directly instead of (ushort)-casting every
		// one (mirrors WriteHeader's Convert.ToUInt16). Constructors can't be generic, so the param is the boxing base
		// Enum - one negligible box per packet. The 16-bit interface opcodes are ushort-backed; a value that overflows
		// ushort throws here (fail-fast) rather than silently truncating.
		public Rpc(ProtoEndpoint sender, ProtoEndpoint receiver, Enum opcode, ReadOnlyMemory<byte> headerData, ReadOnlyMemory<byte> body) : this(sender, receiver, Convert.ToUInt16(opcode), headerData,
			body)
		{
		}

		public Rpc(ProtoEndpoint sender, ProtoEndpoint receiver, Enum opcode, ReadOnlyMemory<byte> headerData, Action<FaolanStream> body) : this(sender, receiver, Convert.ToUInt16(opcode), headerData,
			body)
		{
		}

		public Rpc(ProtoEndpoint sender, ProtoEndpoint receiver, Enum opcode, Action<FaolanStream> body) : this(sender, receiver, Convert.ToUInt16(opcode), body)
		{
		}

		// Byte body, no headerData - for the many sends whose whole body is a single pre-encoded blob (codec.Encode())
		// or a stream built by a helper. Distinct from the Action<FaolanStream> overloads (a byte[] is not a delegate).
		public Rpc(ProtoEndpoint sender, ProtoEndpoint receiver, ushort opcode, ReadOnlyMemory<byte> body) : this(sender, receiver, opcode, default, body)
		{
		}

		public Rpc(ProtoEndpoint sender, ProtoEndpoint receiver, Enum opcode, ReadOnlyMemory<byte> body) : this(sender, receiver, Convert.ToUInt16(opcode), default, body)
		{
		}

		// A fresh reader over the body, for the RECEIVE path (handlers read fields sequentially off it). The send path
		// uses Body directly as immutable memory. Wraps the backing array with NO copy when the body is array-backed
		// (always, for our inbound frames); a new cursor per call, so callers keep their own position.
		public FaolanStream BodyReader()
		{
			return MemoryMarshal.TryGetArray(Body, out var segment) && segment.Array is not null
				? new FaolanStream(segment.Array, segment.Offset, segment.Count)
				: new FaolanStream(Body.ToArray());
		}

		// Message -> wire frame. The ONE place a Conan frame is encoded, and the inverse of Parse (the contract
		// PacketFrameRoundTripTests locks): [u32 length][u32 crc32][u32 headerLen][0x0A sender][0x12 receiver]
		// [u16 opcode][headerData][body], with the length and CRC backfilled once the frame is laid out.
		public byte[] Serialize()
		{
			var sender = Sender.ToProtobuf();
			var receiver = Receiver.ToProtobuf();
			var headerData = HeaderData.Span;

			using var stream = new FaolanStream();
			stream.WriteUInt32(0); // length     - backfilled below
			stream.WriteUInt32(0); // crc32      - backfilled below
			// headerLen = [0x0A][byteLen] + sender + [0x12][byteLen] + receiver + opcode(u16) + headerData
			stream.WriteUInt32((uint)(sizeof(byte) * 2 + sender.Length + sizeof(byte) * 2 + receiver.Length + sizeof(ushort) + headerData.Length));
			stream.WriteByte(0x0A);
			stream.WriteArrayByteLength(sender);
			stream.WriteByte(0x12);
			stream.WriteArrayByteLength(receiver);
			stream.WriteUInt16(Opcode);
			if (headerData.Length > 0)
				stream.WriteArray(headerData);

			stream.WriteArray(Body.Span);

			// Backfill the length (everything after the length field) and the CRC over the whole frame.
			var end = stream.Position;
			stream.Position = 0;
			stream.WriteUInt32(stream.Length - sizeof(uint));
			stream.WriteUInt32(Crc32.Calculate(stream));
			stream.Position = end;

			return stream.ToArray();
		}

		// The INDEPENDENT crc recompute: a frame's crc32 field must equal Crc32 over everything after it (Crc32.Calculate
		// skips the leading length+crc). The guardrail PacketFrameRoundTripTests asserts, so a builder that miswrites a
		// single byte fails there rather than silently on the wire.
		public static bool IsCrcValid(byte[] frame)
		{
			using var stream = new FaolanStream(frame);
			stream.ReadUInt32(); // length
			var crc32 = stream.ReadUInt32();

			return crc32 == Crc32.Calculate(stream);
		}

		// Wire frame -> message. The ONE place a Conan frame is decoded, and the inverse of Serialize. Lenient: the CRC
		// is NOT re-checked (the live wire is trusted once framed, and capture-derived frames may carry a placeholder
		// CRC), so a corrupt frame surfaces where a handler reads a field off it - which the dispatch try/catch turns
		// into a disconnect. IsCrcValid is the independent recompute the round-trip test uses.
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

			return new Rpc(sender, receiver, opcode, headerData, body)
			{
				FrameLength = frame.Length
			};
		}

		public static Rpc Parse(ReadOnlySpan<byte> frame)
		{
			return Parse(frame.ToArray());
		}

		private static byte[] BuildBody(Action<FaolanStream> body)
		{
			using var stream = new FaolanStream();
			body(stream);
			return stream.ToArray();
		}
	}
}
