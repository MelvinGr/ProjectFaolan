using System;
using System.Text;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Crypt
{
	// The per-string LDB decryption key: the `key="..."` attribute the server sends on every `<remoteformat>` /
	// `<localized>` reference into an ENCRYPTED LDB category (the quest text 50300-50306 and the NPC dialogue
	// 50000-50003). Eleven of the client's 158 categories are encrypted, and this is what unlocks them - see
	// docs/rdb/ldb-string-index.md.
	//
	// It is a 20-character base-85 rendering of a 128-bit TEA key: four groups of five characters, each group one
	// key word, most-significant digit first. The client asserts the length outright - `sKey.length() == 20` in
	// `ldbface.cpp:37` - then decodes it in `FUN_00622080` and decrypts TEA-ECB in `FUN_00a8f550`, which is
	// byte-for-byte the Tea.DecryptEcb this project already uses for item templates. So an LDB string decrypts
	// exactly like an item template; only the key's delivery differs (per string on the wire, rather than per
	// template in the RDB).
	//
	// ⚠️ The 85-character alphabet SKIPS 0x3a..0x3e (`:;<=>`) and uses `vwxyz` for digits 25..29 instead. That is
	// not decoration: `<`, `>` and `=` cannot sit unescaped in an XML attribute, and the key travels inside one.
	// The client's decoder expresses the same thing as a fold (`c -= 0x3c` for `v`..`z`), which READS like a
	// many-to-one aliasing - it is not, because the characters it folds from are the only ones ever emitted.
	// Assuming otherwise produces an encoder that emits `:;<=>` and a round-trip that fails on 72% of real keys.
	// Verified: all 168 shipped keys round-trip string -> key -> string EXACTLY (LdbKeyTests).
	public static class LdbKey
	{
		// Digit value -> character. Index IS the digit, so this table is the whole encoding.
		public const string Alphabet = "!\"#$%&'()*+,-./0123456789vwxyz?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstu";

		public const int KeyLength = 20; // 4 words x 5 base-85 digits
		private const int Radix = 85;
		private const int WordDigits = 5;

		// Decodes the 20-character key into the 128-bit TEA key. Accepts the RAW form or the XML-ESCAPED wire form
		// (the attribute is escaped in transit), since callers hold one or the other depending on where they sit.
		// Returns null when the input is not a well-formed key rather than throwing: a capture can carry a truncated
		// or absent attribute, and a missing key means "this string stays encrypted", not "the packet is corrupt".
		public static ProtoDecryptionKey? Decode(string? key)
		{
			var raw = Unescape(key);
			if (raw == null || raw.Length != KeyLength)
				return null;

			var words = new uint[4];
			for (var w = 0; w < 4; w++)
			{
				var value = 0u;
				for (var i = 0; i < WordDigits; i++)
				{
					var digit = Alphabet.IndexOf(raw[w * WordDigits + i]);
					if (digit < 0)
						return null; // not in the alphabet - not a key

					value = unchecked(value * Radix + (uint)digit);
				}

				words[w] = value;
			}

			return new ProtoDecryptionKey
			{
				Key1 = words[0],
				Key2 = words[1],
				Key3 = words[2],
				Key4 = words[3]
			};
		}

		// The exact inverse: the 20-character RAW key. Escape() puts it on the wire.
		public static string Encode(ProtoDecryptionKey? key)
		{
			if (key == null)
				return "";

			var sb = new StringBuilder(KeyLength);
			Span<char> digits = stackalloc char[WordDigits]; // hoisted: a stackalloc inside the loop would grow the frame per word
			foreach (var word in new[] { key.Key1, key.Key2, key.Key3, key.Key4 })
			{
				var value = word;
				for (var i = WordDigits - 1; i >= 0; i--)
				{
					digits[i] = Alphabet[(int)(value % Radix)];
					value /= Radix;
				}

				sb.Append(digits);
			}

			return sb.ToString();
		}

		// The five characters retail escapes in the attribute, and the entity it uses for each. Measured across the
		// corpus, where the split is CLEAN - a character is either always raw or always escaped, never both - so this
		// map reproduces the captured bytes rather than approximating them. Note `$` and `\` use numeric entities
		// even though XML does not require escaping either; that is retail's spelling and we match it.
		public static string Escape(string raw)
		{
			var sb = new StringBuilder(raw.Length);
			foreach (var c in raw)
			{
				sb.Append(c switch
				{
					'&' => "&amp;", // first in the switch, but order is irrelevant - we never re-escape output
					'"' => "&quot;",
					'\'' => "&#39;",
					'$' => "&#36;",
					'\\' => "&#92;",
					_ => c.ToString()
				});
			}

			return sb.ToString();
		}

		// Reverses Escape. Returns the input unchanged when it carries no entity, so a raw key passes through.
		public static string? Unescape(string? escaped)
		{
			if (escaped == null)
				return null;
			if (escaped.IndexOf('&') < 0)
				return escaped;

			return escaped
				.Replace("&quot;", "\"")
				.Replace("&#39;", "'")
				.Replace("&#36;", "$")
				.Replace("&#92;", "\\")
				.Replace("&amp;", "&"); // LAST: an escaped '&' would otherwise re-introduce the other entities
		}

		// The wire form of a stored key: encode, then escape. This is what goes into `key="..."`.
		public static string ToWire(ProtoDecryptionKey? key)
		{
			return key == null
				? ""
				: Escape(Encode(key));
		}
	}
}
