using System;

namespace Faolan.Core.Extensions
{
	// Thrown by SpanReader, and therefore by every codec's Parse. A codec's reader is STRICT: a body that is short,
	// overlong, carries the wrong type word, or contains a field the grammar cannot account for is a decode FAILURE,
	// not a value to be defaulted or a `false` to be dropped on the floor.
	//
	// The reason is the project's standing one (see the `tolerant_readers_hide_wire_bugs` and
	// `no_fallbacks_surface_errors` notes): a reader that shrugs at bytes it does not understand cannot tell a
	// correct grammar from a wrong one, so a mis-decode survives review, survives the tests, and shows up as a
	// client that silently renders the wrong thing. A throw names the offset and what was expected, which turns
	// "the buff icon is missing" into "MagicBuffCodec wanted 4 bytes at 22, body has 2".
	//
	// Lives in Faolan.Core (not Faolan.Core.Network.Codecs, where the codecs are) because SpanReader is here and
	// Faolan.Core is the BASE layer with no project references - it cannot throw a type defined one layer up.
	public sealed class CodecParseException : Exception
	{
		public CodecParseException(string message) : base(message)
		{
		}

		private CodecParseException(string? codec, int offset, string message) : base(Describe(codec, offset, message))
		{
			Codec = codec;
			Offset = offset;
		}

		// The codec that was reading, when it identified itself to the SpanReader. Null is fine - the stack trace
		// names it too; this is for the message.
		public string? Codec { get; }

		// Byte offset into the body being parsed, at the point the read failed.
		public int Offset { get; }

		// A field ran past the end of the body.
		public static CodecParseException Underrun(string? codec, int offset, int needed, int available)
		{
			return new CodecParseException(codec, offset, $"needed {needed} byte(s) but only {available} remain");
		}

		// A field held a value the grammar does not allow (a wrong type word, an invalid list frame, ...).
		public static CodecParseException Unexpected(string? codec, int offset, string expected, string actual)
		{
			return new CodecParseException(codec, offset, $"expected {expected}, got {actual}");
		}

		// The grammar completed but bytes were left over. This is the one that catches a PARTIAL decode - a codec
		// that reads the three fields it knows about and ignores the rest looks correct until you ask it to account
		// for the whole body.
		public static CodecParseException Trailing(string? codec, int offset, int remaining, string preview)
		{
			return new CodecParseException(codec, offset, $"{remaining} unread byte(s) after the last modelled field: {preview}");
		}

		private static string Describe(string? codec, int offset, string message)
		{
			return codec is null ? $"malformed body at offset {offset}: {message}" : $"{codec}: malformed body at offset {offset}: {message}";
		}
	}
}
