using System;

namespace Faolan.Core.Extensions
{
	/// <summary>
	///     Thrown by <see cref="SpanReader" />, and therefore by every codec's <c>Parse</c>.
	/// </summary>
	/// <remarks>
	///     A codec's reader is strict: a body that is short, overlong, carries the wrong type word, or contains a
	///     field the grammar cannot account for is a decode failure, not a value to be defaulted or a <c>false</c> to
	///     be dropped on the floor. A reader that shrugs at bytes it does not understand cannot tell a correct grammar
	///     from a wrong one, so a mis-decode survives review, survives the tests, and shows up as a client that
	///     silently renders the wrong thing. A throw names the offset and what was expected, which turns "the buff
	///     icon is missing" into "MagicBuffCodec wanted 4 bytes at 22, body has 2".
	///     <para />
	///     Lives in Faolan.Core rather than beside the codecs because <see cref="SpanReader" /> is here and
	///     Faolan.Core is the base layer with no project references - it cannot throw a type defined one layer up.
	/// </remarks>
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

		/// <summary>
		///     The codec that was reading, when it identified itself to the <see cref="SpanReader" />. Null is fine -
		///     the stack trace names it too; this is for the message.
		/// </summary>
		public string? Codec { get; }

		/// <summary>Byte offset into the body being parsed, at the point the read failed.</summary>
		public int Offset { get; }

		/// <summary>A field ran past the end of the body.</summary>
		public static CodecParseException Underrun(string? codec, int offset, int needed, int available)
		{
			return new CodecParseException(codec, offset, $"needed {needed} byte(s) but only {available} remain");
		}

		/// <summary>A field held a value the grammar does not allow (a wrong type word, an invalid list frame, ...).</summary>
		public static CodecParseException Unexpected(string? codec, int offset, string expected, string actual)
		{
			return new CodecParseException(codec, offset, $"expected {expected}, got {actual}");
		}

		/// <summary>
		///     The grammar completed but bytes were left over. This is the one that catches a partial decode - a codec
		///     that reads the three fields it knows about and ignores the rest looks correct until it is asked to
		///     account for the whole body.
		/// </summary>
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
