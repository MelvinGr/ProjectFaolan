using System;
using System.Text;

namespace Faolan.Core.Crypt
{
	// The classic UNIX ELF/PJW string hash masked to 28 bits - the client's other string hash, beside
	// <see cref="Bkdr31"/>. It is what keys the localized string tables: an LDB entry's id is the hash of its token,
	// not a sequence number, which is why a string can be looked up from a token nobody stored.
	//
	//   elf28("DisplayedText(3)|GenericDisplayAreaName|3474586") == 19303910 == cat 50025 "Castle Approach"
	//
	// Masked to 28 bits, so there are only 2^28 buckets. Hashing N candidates against a table of M ids produces
	// roughly N*M/2^28 collisions by chance, and against a table of place names a collision looks exactly as
	// plausible as a hit. A hash match is a candidate, never a proof - see AreaBannerNames, which inverts this hash
	// over a token parameter and then throws away about half its hits.
	//
	// It hashes Latin-1 bytes, which is the client's own string encoding, not UTF-16 chars. Every token in play is
	// ASCII so the two agree, but a hash re-implemented per caller can disagree with itself, which is why this is the
	// one implementation. Bkdr31 next door carries the same rule for the same reason.
	public static class Elf28
	{
		public static uint Of(string value)
		{
			return Of(value, 0);
		}

		// Resumable, so a constant token prefix is hashed once and only the varying tail is paid per candidate. That
		// is what makes a sweep of tens of millions cost a second rather than a minute.
		public static uint Of(string value, uint state)
		{
			foreach (var b in Encoding.Latin1.GetBytes(value))
				state = Step(state, b);

			return state;
		}

		// `Of(n.ToString(), state)` without allocating the string - a sweep runs this tens of millions of times.
		public static uint OfDigits(uint value, uint state)
		{
			Span<char> digits = stackalloc char[10];
			var at = digits.Length;
			do
			{
				digits[--at] = (char)('0' + (int)(value % 10));
				value /= 10;
			} while (value > 0);

			for (var i = at; i < digits.Length; i++)
				state = Step(state, digits[i]);

			return state;
		}

		// The allocation-free form, for a sweep that hashes the same tail against millions of prefix states.
		// `Of(string, uint)` goes through `Encoding.Latin1.GetBytes`, which allocates per call - fine for one token,
		// ruinous for 10^8 of them. Latin-1 is the char's low byte, so a span walk is the same bytes with no array.
		public static uint Of(ReadOnlySpan<char> value, uint state)
		{
			foreach (var c in value)
				state = Step(state, (byte)c);

			return state;
		}

		private static uint Step(uint h, uint b)
		{
			h = (h << 4) + b;
			return (h ^ ((h & 0xF0000000) >> 24)) & 0x0FFFFFFF;
		}
	}
}
