using System;
using Faolan.Core.Enums;
using Faolan.Core.Network.Packets.ChatServer;
using Xunit;

namespace Faolan.Tests
{
	// ChatServerMessage is the chat tier's whole wire model - build, serialize and parse in one type, as Rpc is one tier
	// down. These pin the frame layout [u16 opcode][u16 bodyLen][body] and that Serialize/Parse are mutual inverses,
	// which is what stops a send and a receive drifting apart.
	public class ChatServerMessageTests
	{
		[Fact]
		public void Serialize_FramesOpcodeAndBodyLength()
		{
			var frame = new ChatServerMessage(ChatServerInterfaceOpcodes.ChatSenderInfo, s => s.WriteUInt32(0x01313438).WriteString("Conan")).Serialize();

			Assert.Equal("0014", Convert.ToHexString(frame.AsSpan(0, 2)).ToLowerInvariant());
			Assert.Equal(frame.Length - 4, (frame[2] << 8) | frame[3]); // bodyLen counts the bytes AFTER the 4-byte header
		}

		[Fact]
		public void SerializeAndParse_AreMutualInverses()
		{
			var message = new ChatServerMessage(ChatServerInterfaceOpcodes.SystemMessage, s => s.WriteUInt16(0x0000).WriteString("hello").WriteUInt16(0x0000));
			var frame = message.Serialize();

			var parsed = ChatServerMessage.Parse(frame);

			Assert.Equal(message.Opcode, parsed.Opcode);
			Assert.Equal(message.Body.ToArray(), parsed.Body.ToArray());
			Assert.Equal(frame.Length, parsed.FrameLength);
			Assert.Equal(frame, parsed.Serialize());
		}

		// The reader is a fresh cursor over the body per call, so a handler reads its fields sequentially off one.
		[Fact]
		public void BodyReader_ReadsTheBodyFields()
		{
			var parsed = ChatServerMessage.Parse(new ChatServerMessage(ChatServerInterfaceOpcodes.ChatSenderInfo, s => s.WriteUInt32(0x01313438).WriteString("Conan")).Serialize());

			var body = parsed.BodyReader();
			Assert.Equal(0x01313438u, body.ReadUInt32());
			Assert.Equal("Conan", body.ReadString());
		}

		// An empty body frames as a bare 4-byte header - the shape of the unnamed 0x0005 beat on chat connect.
		[Fact]
		public void EmptyBody_IsTheBareHeader()
		{
			Assert.Equal([0x00, 0x05, 0x00, 0x00], new ChatServerMessage(0x0005).Serialize());
			Assert.Equal(0, ChatServerMessage.Parse([0x00, 0x05, 0x00, 0x00]).Body.Length);
		}

		// Parse is lenient like the Conan parser: a body shorter than its declared length is taken as-is, so a malformed
		// frame surfaces where a handler reads a field off it rather than throwing in the transport.
		[Fact]
		public void Parse_ShortBody_TakesWhatIsThere()
		{
			var parsed = ChatServerMessage.Parse([0x00, 0x23, 0x00, 0x40, 0xAA, 0xBB]); // declares 0x40 bytes, carries 2

			Assert.Equal([0xAA, 0xBB], parsed.Body.ToArray());
		}
	}
}
