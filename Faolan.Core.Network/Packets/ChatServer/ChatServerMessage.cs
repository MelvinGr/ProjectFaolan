using System;
using System.Runtime.InteropServices;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Packets.ChatServer
{
	// The structured form of a chat frame - [u16 opcode][u16 bodyLength][body] - in BOTH directions: what a handler
	// builds and hands to the transport, and what an inbound frame parses into. The chat tier's counterpart to Rpc
	// (Gitea #406), modelled the same way for the same reasons: one type per protocol rather than a build path and a
	// parse path that can disagree, and no re-parse to trace or inspect a message that was just built.
	//
	// It is NOT an Rpc: this wire carries no RpcHeader - no sender/receiver endpoints, no CRC, no interface
	// multiplexing - so the opcode alone identifies the method, in a single per-direction opcode space
	// (ChatServerOpcodes inbound, ChatServerInterfaceOpcodes outbound).
	public readonly record struct ChatServerMessage(ushort Opcode, ReadOnlyMemory<byte> Body = default)
	{
		private const int HeaderSize = sizeof(ushort) * 2;

		// The total wire frame length. Stamped by Parse; 0 on a freshly built message, whose size isn't known until it
		// is serialized. The packet trace reports it as packetLength (mirrors Rpc.FrameLength).
		public int FrameLength { get; init; }

		// Build-side ctors, mirroring Rpc's: an outbound opcode is always a ChatServerInterfaceOpcodes member, and a
		// body is either pre-encoded bytes or built inline with the fluent writers -
		//   new ChatServerMessage(ChatServerInterfaceOpcodes.SystemMessage, s => s.WriteUInt16(0).WriteString(text))
		// - with the scratch stream created and DISPOSED here rather than at every call site.
		public ChatServerMessage(ChatServerInterfaceOpcodes opcode, ReadOnlyMemory<byte> body = default) : this((ushort)opcode, body)
		{
		}

		public ChatServerMessage(ChatServerInterfaceOpcodes opcode, Action<FaolanStream> body) : this((ushort)opcode, BuildBody(body))
		{
		}

		// The ushort overloads are for the frames whose opcode isn't recovered yet (the 0x0005 chat-connect beat), so
		// they stay honest hex instead of being given an invented name in the enum.
		public ChatServerMessage(ushort opcode, Action<FaolanStream> body) : this(opcode, BuildBody(body))
		{
		}

		// A fresh reader over the body, for the RECEIVE path (handlers read fields sequentially off it). A new cursor
		// per call, so callers keep their own position - one `var body = packet.BodyReader();` per handler.
		public FaolanStream BodyReader()
		{
			return MemoryMarshal.TryGetArray(Body, out var segment) && segment.Array is not null
				? new FaolanStream(segment.Array, segment.Offset, segment.Count)
				: new FaolanStream(Body.ToArray());
		}

		// Message -> frame. The ONE place a chat frame is encoded; the length field counts the BODY, so a frame is
		// bodyLength + this 4-byte header (matches the retail captures).
		public byte[] Serialize()
		{
			var frame = new byte[HeaderSize + Body.Length];
			frame.WriteU16Be(0, Opcode);
			frame.WriteU16Be(sizeof(ushort), (ushort)Body.Length);
			Body.Span.CopyTo(frame.AsSpan(HeaderSize));

			return frame;
		}

		// Frame -> message. Lenient like Rpc.Parse: a body shorter than its declared length is taken as-is
		// rather than throwing, so a malformed frame surfaces where a handler reads a field off it (and the dispatch
		// try/catch turns that into a disconnect) instead of at two different layers. The framer only ever emits
		// complete frames, so this clamp is a backstop, not the normal path.
		public static ChatServerMessage Parse(byte[] frame)
		{
			var opcode = frame.ReadU16Be(0);
			var declared = frame.ReadU16Be(sizeof(ushort));

			return new ChatServerMessage(opcode, frame.AsMemory(HeaderSize, Math.Min(declared, frame.Length - HeaderSize)))
			{
				FrameLength = frame.Length
			};
		}

		private static byte[] BuildBody(Action<FaolanStream> body)
		{
			using var stream = new FaolanStream();
			body(stream);

			return stream.ToArray();
		}
	}
}
