using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Text
{
	// Whisper (0x001E) - a tell. The SAME body both ways, which is why one codec serves both:
	//   [u32 peerCharacterId][u16 len][text][u8 0][u8 1][u8 0]
	// The peer slot carries the TARGET's id in the client's request and the SENDER's id on delivery - the shape does
	// not distinguish them, the direction does. Byte-exact against the whisper capture (WhisperTests); the receiving
	// client resolves the name from the ChatSenderInfo (0x0014) that precedes it.
	//
	// Moved in from `ChatServerListener.BuildWhisper` in #530.
	public static class WhisperCodec
	{
		public const ushort Opcode = (ushort)ChatServerInterfaceOpcodes.Whisper;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(Whisper value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static byte[] Build(uint peerCharacterId, string text)
		{
			return Build(new Whisper(peerCharacterId, text));
		}

		public static Whisper Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(WhisperCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static Whisper Read(ref SpanReader r)
		{
			var peer = r.ReadU32Be();
			var text = r.ReadStringU16Length();
			r.ExpectU8(0x00, "tell trailer byte 0");
			r.ExpectU8(0x01, "tell trailer byte 1");
			r.ExpectU8(0x00, "tell trailer byte 2");

			return new Whisper(peer, text);
		}

		internal static void Write(FaolanStream s, Whisper v)
		{
			s.WriteUInt32(v.PeerCharacterId).WriteString(v.Text).WriteByte(0x00).WriteByte(0x01).WriteByte(0x00);
		}
	}

	// Whisper (0x001E), both directions.
	public sealed record Whisper(uint PeerCharacterId, string Text);
}
