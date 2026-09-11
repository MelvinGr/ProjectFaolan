using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Text
{
	/// <summary>
	///     <c>SystemMessage</c> - the chat tier's server line (the login welcome, GM feedback, the build string):
	///     <c>[u16 0][u16 len][text][u16 0]</c>.
	/// </summary>
	/// <remarks>
	///     There is no sender at all, which is what distinguishes it from a CharacterChat carrying server text: the client
	///     renders it in its system colour without needing a <c>ChatSenderInfo</c> first. Named
	///     <c>ChatSystemMessage*</c> rather than <c>SystemMessage*</c> because it is the chat tier's message; the
	///     on-screen feedback overlay is <see cref="FormatFeedbackCodec" /> and the two are routinely confused.
	/// </remarks>
	public static class ChatSystemMessageCodec
	{
		public const ushort Opcode = (ushort)ChatServerInterfaceOpcodes.SystemMessage;

		// Canonical: the only method here that writes bytes.
		public static byte[] Build(ChatSystemMessage value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static byte[] Build(string text)
		{
			return Build(new ChatSystemMessage(text));
		}

		public static ChatSystemMessage Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(ChatSystemMessageCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static ChatSystemMessage Read(ref SpanReader r)
		{
			r.ExpectU16Be(0, "the leading word");
			var text = r.ReadStringU16Length();
			r.ExpectU16Be(0, "the trailing word");

			return new ChatSystemMessage(text);
		}

		internal static void Write(FaolanStream s, ChatSystemMessage v)
		{
			s.WriteUInt16(0).WriteString(v.Text).WriteUInt16(0);
		}
	}

	/// <summary>The SystemMessage body, on the chat tier.</summary>
	public sealed record ChatSystemMessage(string Text);
}
