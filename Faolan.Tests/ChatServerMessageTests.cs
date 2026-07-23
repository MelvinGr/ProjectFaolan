using System;
using Faolan.Core.Enums;
using Faolan.Core.Network.Packets.ChatServer;
using Xunit;

namespace Faolan.Tests
{
	// The chat wire frame is [u16 opcode][u16 bodyLen][body]. ChatServerMessage.Build is the one place a send frames it
	// (the chat tier's PacketSerializer), so it is pinned against the parser that reads the same frame back.
	public class ChatServerMessageTests
	{
		[Fact]
		public void Build_FramesOpcodeAndBodyLength()
		{
			var frame = ChatServerMessage.Build(ChatServerInterfaceOpcodes.ChatSenderInfo, s => s.WriteUInt32(0x01313438).WriteString("Conan"));

			Assert.Equal("0014", Convert.ToHexString(frame.AsSpan(0, 2)).ToLowerInvariant());
			Assert.Equal(frame.Length - 4, (frame[2] << 8) | frame[3]); // bodyLen counts the bytes AFTER the 4-byte header
		}

		// Round-trips through the inbound parser: opcode and body survive, so the builder and ChatServerPacket agree on
		// the frame layout.
		[Fact]
		public void Build_RoundTripsThroughTheParser()
		{
			var frame = ChatServerMessage.Build(ChatServerInterfaceOpcodes.SystemMessage, s => s.WriteUInt16(0x0000).WriteString("hello").WriteUInt16(0x0000));

			var parsed = new ChatServerPacket(frame);
			Assert.True(parsed.IsValid);
			Assert.Equal((ushort)ChatServerInterfaceOpcodes.SystemMessage, (ushort)parsed.Opcode);
			Assert.Equal(frame.Length - 4u, parsed.Length);
		}

		// An empty body frames as a bare 4-byte header - the shape of the unnamed 0x0005 beat on chat connect. (The
		// inbound parser rejects a 0-length body, which is an inbound guard: nothing the server sends is parsed back.)
		[Fact]
		public void Build_WithNoBody_IsTheBareHeader()
		{
			Assert.Equal([0x00, 0x05, 0x00, 0x00], ChatServerMessage.Build(0x0005));
		}
	}
}
