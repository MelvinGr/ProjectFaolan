using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Text
{
	// Builds/parses the ChatTextIIR (sub-opcode 0xE0B51102) body - a WORLD/SYSTEM chat line the server pushes into
	// the client's chat window (distinct from FormatFeedbackIIR's on-screen FIFO overlay and from the ChatServer
	// channel chat). The text is a `remoteformat` (localized tokens + parameters), the same XML shape the feedback
	// and Knubot packets use, so the server can emit a localized system line without literal strings.
	//
	// Carried as an IIR on the WORLD conversation (opcode 0x2000; sender = GameServerListener.WorldSenderEndpoint, receiver = the
	// player's GameInterface = GameInterfaceType + ClientInstance). The IIR body's `instance` is that receiver
	// instance, so a live send is fully addressable from the session.
	//
	// Wire grammar (big-endian, decoded 2026-06-16 from DemoL80 randomActions #7564, golden-tested in
	// ChatTextCodecTests). The carrier's [u32 len] belongs to the SENDER (#530 rule 2) - WorldIir.Send writes it:
	//   [u32 0xE0B51102]   ChatTextIIR sub-opcode
	//   [u32 0x0000C350]   standard IIR marker (Character class)
	//   [u32 instance]     the target GameInterface object (= the receiver endpoint instance / ClientInstance)
	//   [u8  0x00]         lead flag (constant in the sole capture)
	//   [u16 xmlLen][remoteformat XML]      the message (no null terminator)
	//   [u8  chatTextType] NPCChatTextType_e — the chat-text type enum (0 in our capture; the client reader rejects
	//                      values > 5). Decoded from the client #321: writer FUN_006a4c70 emits byte(this+0x4c) then
	//                      the u32 below; reader FUN_006a4c00 stores the byte into +0x4c after a `<= 5` bounds check.
	//   [u32 typeFlags]    big-endian flags/format word (client field this+0x2c; 0x40000001 in our capture). Read as
	//                      a u32 by FUN_0048f900; same bit-30 (0x40000000) family as FormatFeedbackIIR.typeFlags —
	//                      exact bit meanings still loose, but it is a real field, not padding.
	public static class ChatTextCodec
	{
		// The two fields after the XML, decoded from the client (de)serializer (#321), no longer an opaque "trailer":
		// the chat-text TYPE selector (NPCChatTextType_e, one byte) then a big-endian flags/format word. Defaults are
		// the values in our sole capture (type 0, flags 0x40000001).
		public const byte DefaultChatTextType = 0x00;
		public const uint DefaultTypeFlags = 0x40000001;
		public const uint Opcode = (uint)GameServer0X2000Opcodes.ChatTextIIR; // 0xE0B51102
		private const byte LeadFlag = 0x00;

		public static byte[] Build(ChatTextIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static byte[] Build(uint instance, byte[] xml)
		{
			return Build(new ChatTextIIR(instance, xml));
		}

		public static ChatTextIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(ChatTextCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static ChatTextIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "the ChatTextIIR sub-opcode");
			r.ExpectU32Be(EntityClass.Character, "the character entity class");
			var instance = r.ReadU32Be();
			r.ExpectU8(LeadFlag, "the lead flag");
			var xml = r.ReadBytesU16Length().ToArray();

			return new ChatTextIIR(instance, xml, r.ReadU8(), r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, ChatTextIIR v)
		{
			s.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(v.Instance)
				.WriteByte(LeadFlag)
				.WriteUInt16(v.Xml.Length)
				.WriteArray(v.Xml)
				.WriteByte(v.ChatTextType)
				.WriteUInt32(v.TypeFlags);
		}
	}

	public sealed record ChatTextIIR(uint Instance, byte[] Xml, byte ChatTextType = ChatTextCodec.DefaultChatTextType, uint TypeFlags = ChatTextCodec.DefaultTypeFlags);
}
