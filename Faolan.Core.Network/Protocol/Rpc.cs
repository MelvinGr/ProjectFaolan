using System;
using System.Runtime.InteropServices;
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
		// Body-building overloads: build the body inline with the fluent FaolanStream writers, e.g.
		//   new Rpc(sender, receiver, opcode, body => body.WriteUInt32(x).WriteArrayUInt32Length(blob))
		// instead of newing + disposing a stream at every call site. The scratch stream is created and DISPOSED here
		// (unlike the old fluent PacketStream send pattern). The Action<FaolanStream> is distinct from the primary
		// ctor's ReadOnlyMemory<byte> Body, so there is no overload ambiguity.
		public Rpc(ProtoEndpoint sender, ProtoEndpoint receiver, ushort opcode, ReadOnlyMemory<byte> headerData, Action<FaolanStream> body)
			: this(sender, receiver, opcode, headerData, BuildBody(body))
		{
		}

		public Rpc(ProtoEndpoint sender, ProtoEndpoint receiver, ushort opcode, Action<FaolanStream> body)
			: this(sender, receiver, opcode, default, BuildBody(body))
		{
		}

		// Enum-opcode overloads so call sites pass GameInterfaceOpcodes.Foo directly instead of (ushort)-casting every
		// one (mirrors WriteHeader's Convert.ToUInt16). Constructors can't be generic, so the param is the boxing base
		// Enum - one negligible box per packet. The 16-bit interface opcodes are ushort-backed; a value that overflows
		// ushort throws here (fail-fast) rather than silently truncating.
		public Rpc(ProtoEndpoint sender, ProtoEndpoint receiver, Enum opcode, ReadOnlyMemory<byte> headerData, ReadOnlyMemory<byte> body)
			: this(sender, receiver, Convert.ToUInt16(opcode), headerData, body)
		{
		}

		public Rpc(ProtoEndpoint sender, ProtoEndpoint receiver, Enum opcode, ReadOnlyMemory<byte> headerData, Action<FaolanStream> body)
			: this(sender, receiver, Convert.ToUInt16(opcode), headerData, body)
		{
		}

		public Rpc(ProtoEndpoint sender, ProtoEndpoint receiver, Enum opcode, Action<FaolanStream> body)
			: this(sender, receiver, Convert.ToUInt16(opcode), body)
		{
		}

		// Byte body, no headerData - for the many sends whose whole body is a single pre-encoded blob (codec.Encode())
		// or a stream built by a helper. Distinct from the Action<FaolanStream> overloads (a byte[] is not a delegate).
		public Rpc(ProtoEndpoint sender, ProtoEndpoint receiver, ushort opcode, ReadOnlyMemory<byte> body)
			: this(sender, receiver, opcode, default, body)
		{
		}

		public Rpc(ProtoEndpoint sender, ProtoEndpoint receiver, Enum opcode, ReadOnlyMemory<byte> body)
			: this(sender, receiver, Convert.ToUInt16(opcode), default, body)
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

		private static byte[] BuildBody(Action<FaolanStream> body)
		{
			using var stream = new FaolanStream();
			body(stream);
			return stream.ToArray();
		}
	}
}
