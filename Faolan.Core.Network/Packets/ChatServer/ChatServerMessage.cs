using System;
using System.Runtime.InteropServices;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Packets.ChatServer
{
	/// <summary>
	///     The structured form of a chat frame - <c>[u16 opcode][u16 bodyLength][body]</c> - in both directions: what a
	///     handler builds and hands to the transport, and what an inbound frame parses into.
	/// </summary>
	/// <remarks>
	///     The chat tier's counterpart to <c>Rpc</c>, modelled the same way: one type per protocol rather than a build
	///     path and a parse path that can disagree, and no re-parse to trace or inspect a message that was just built.
	///     <para>
	///         It is not an Rpc - this wire carries no RpcHeader, so no sender/receiver endpoints, no CRC and no
	///         interface multiplexing - so the opcode alone identifies the method, in a single per-direction opcode
	///         space (<c>ChatServerOpcodes</c> inbound, <c>ChatServerInterfaceOpcodes</c> outbound).
	///     </para>
	/// </remarks>
	public readonly record struct ChatServerMessage(ushort Opcode, ReadOnlyMemory<byte> Body = default)
	{
		private const int HeaderSize = sizeof(ushort) * 2;

		// The total wire frame length. Stamped by Parse, and 0 on a freshly built message whose size is not known until
		// it is serialized. The packet trace reports it as packetLength.
		public int FrameLength { get; init; }

		// Build-side constructors: an outbound opcode is always a ChatServerInterfaceOpcodes member, and a body is
		// either pre-encoded bytes or built inline with the fluent writers -
		//   new ChatServerMessage(ChatServerInterfaceOpcodes.SystemMessage, s => s.WriteUInt16(0).WriteString(text))
		// - with the scratch stream created and disposed here rather than at every call site.
		public ChatServerMessage(ChatServerInterfaceOpcodes opcode, ReadOnlyMemory<byte> body = default) : this((ushort)opcode, body)
		{
		}

		public ChatServerMessage(ChatServerInterfaceOpcodes opcode, Action<FaolanStream> body) : this((ushort)opcode, BuildBody(body))
		{
		}

		// The ushort overloads are for frames whose opcode is not recovered yet (the 0x0005 chat-connect beat), so they
		// stay as hex rather than being given an invented name in the enum.
		public ChatServerMessage(ushort opcode, Action<FaolanStream> body) : this(opcode, BuildBody(body))
		{
		}

		/// <summary>
		///     A fresh reader over the body, for the receive path where handlers read fields sequentially off it. A new
		///     cursor per call, so callers keep their own position.
		/// </summary>
		public FaolanStream BodyReader()
		{
			return MemoryMarshal.TryGetArray(Body, out var segment) && segment.Array is not null
				? new FaolanStream(segment.Array, segment.Offset, segment.Count)
				: new FaolanStream(Body.ToArray());
		}

		/// <summary>
		///     Message to frame - the one place a chat frame is encoded. The length field counts the body, so a frame is
		///     bodyLength plus the 4-byte header, matching the retail captures.
		/// </summary>
		public byte[] Serialize()
		{
			var frame = new byte[HeaderSize + Body.Length];
			frame.WriteU16Be(0, Opcode);
			frame.WriteU16Be(sizeof(ushort), (ushort)Body.Length);
			Body.Span.CopyTo(frame.AsSpan(HeaderSize));

			return frame;
		}

		/// <summary>
		///     Frame to message. Lenient like <c>Rpc.Parse</c>: a body shorter than its declared length is taken as-is
		///     rather than throwing, so a malformed frame surfaces where a handler reads a field off it and the dispatch
		///     try/catch turns that into a disconnect.
		/// </summary>
		/// <remarks>The framer only emits complete frames, so the clamp is a backstop rather than the normal path.</remarks>
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
