using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Text
{
	/// <summary>
	///     The <c>ChatTextIIR</c> body - a world or system chat line the server pushes into the client's chat window,
	///     distinct from FormatFeedbackIIR's on-screen FIFO overlay and from the ChatServer channel chat.
	/// </summary>
	/// <remarks>
	///     The text is a <c>remoteformat</c> (localized tokens plus parameters), the same XML shape the feedback and
	///     Knubot packets use, so the server can emit a localized system line without literal strings.
	///     <para>
	///         It is carried as an IIR on the world conversation (sender the world endpoint, receiver the player's
	///         GameInterface). The IIR body's <c>instance</c> is that receiver instance, so a live send is fully
	///         addressable from the session.
	///     </para>
	///     <para>
	///         Wire grammar, big-endian, decoded from the DemoL80 randomActions capture and golden-tested in
	///         <c>ChatTextCodecTests</c>. The carrier's <c>[u32 len]</c> belongs to the sender:
	///         <code>
	///         [u32 subop]        ChatTextIIR sub-opcode
	///         [u32 entityClass]  standard IIR marker (Character class)
	///         [u32 instance]     the target GameInterface object (the receiver endpoint instance / ClientInstance)
	///         [u8  0x00]         lead flag (constant in the sole capture)
	///         [u16 xmlLen][remoteformat XML]   the message, with no null terminator
	///         [u8  chatTextType] NPCChatTextType_e - 0 in the capture; the client reader rejects values above 5
	///         [u32 typeFlags]    big-endian flags/format word (client field this+0x2c; 0x40000001 in the capture)
	///         </code>
	///         The type byte comes from the client's writer FUN_006a4c70 and reader FUN_006a4c00, which stores it into
	///         +0x4c after a bounds check. <c>typeFlags</c> is read as a u32 by FUN_0048f900 and is the same bit-30 family
	///         as FormatFeedbackIIR's <c>typeFlags</c>; the exact bit meanings are still loose, but it is a real field
	///         rather than padding.
	///     </para>
	/// </remarks>
	public static class ChatTextCodec
	{
		// The two fields after the XML: the chat-text type selector (NPCChatTextType_e, one byte) then a big-endian
		// flags/format word. The defaults are the values in the sole capture (type 0, flags 0x40000001).
		public const byte DefaultChatTextType = 0x00;
		public const uint DefaultTypeFlags = 0x40000001;
		public const uint Opcode = (uint)IirTypes.ChatTextIIR; // 0xE0B51102
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
