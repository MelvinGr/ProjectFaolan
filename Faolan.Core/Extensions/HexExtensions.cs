using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;

namespace Faolan.Core.Extensions
{
	public static class HexExtensions
	{
		/// <summary>Inverse of <see cref="ToU32Be" />: the u32 as 8 lowercase hex chars, most significant byte first.</summary>
		public static string FromU32Be(uint value)
		{
			Span<byte> bytes = stackalloc byte[4];
			BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
			return Convert.ToHexStringLower(bytes);
		}

		/// <summary>Inverse of <see cref="ToU32Le" />: the u32's four bytes in memory order, as 8 lowercase hex chars.</summary>
		public static string FromU32Le(uint value)
		{
			Span<byte> bytes = stackalloc byte[4];
			BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
			return Convert.ToHexStringLower(bytes);
		}

		/// <summary>
		///     Hex -> <see cref="BigInteger" />, forced POSITIVE: a leading high bit would otherwise read as a negative
		///     two's-complement value and every <c>ModPow</c> over it would be wrong.
		/// </summary>
		public static BigInteger ToBigInteger(string hex)
		{
			return BigInteger.Parse("0" + hex.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
		}

		/// <summary>
		///     Minimal lowercase hex - no sign nibble, no leading zeros - which is how the client writes a
		///     <see cref="BigInteger" />. Inverse of <see cref="ToBigInteger" />; left-pad the result where a fixed
		///     field width is wanted.
		/// </summary>
		public static string ToHex(BigInteger value)
		{
			// "x" on a BigInteger emits a leading 0 nibble whenever the high bit is set, so that parsing it back
			// cannot read as negative. TrimStart drops it along with any other leading zeros.
			var hex = value.ToString("x", CultureInfo.InvariantCulture).TrimStart('0');
			return hex.Length == 0
				? "0"
				: hex;
		}

		public static string ToHex(this BigInteger value, int padToLength)
		{
			return ToHex(value).PadLeft(padToLength, '0');
		}

		/// <summary>
		///     8 hex chars -> 4 bytes -> u32, reading the string's FIRST byte as the value's most significant - the
		///     plain reading, and what <c>%08x</c> on the far side produced.
		/// </summary>
		/// <exception cref="FormatException">Not exactly 8 hex characters.</exception>
		public static uint ToU32Be(ReadOnlySpan<char> hex)
		{
			Span<byte> bytes = stackalloc byte[4];
			ToBytes(hex, bytes);
			return BinaryPrimitives.ReadUInt32BigEndian(bytes);
		}

		/// <summary>
		///     8 hex chars -> 4 bytes b0 b1 b2 b3 -> u32 <c>b0 | b1&lt;&lt;8 | b2&lt;&lt;16 | b3&lt;&lt;24</c>. The hex
		///     spells the bytes in memory order, so the WORD comes out byte-swapped - the client's <c>popHexInt</c>.
		/// </summary>
		/// <exception cref="FormatException">Not exactly 8 hex characters.</exception>
		public static uint ToU32Le(ReadOnlySpan<char> hex)
		{
			Span<byte> bytes = stackalloc byte[4];
			ToBytes(hex, bytes);
			return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
		}

		// Strict on purpose: FromHexString rejects a stray space and a "0x" prefix, and the length check rejects a
		// short word - all of which Convert.ToUInt32(s, 16) would have quietly accepted. Callers decrypt whatever
		// comes back, so a body that isn't hex has to fail HERE rather than turn into a plausible-looking word.
		// The span IS the value (not a field at an offset), so BinaryPrimitives is right and SpanExtensions is not.
		private static void ToBytes(ReadOnlySpan<char> hex, Span<byte> destination)
		{
			if (hex.Length != destination.Length * 2 || Convert.FromHexString(hex, destination, out _, out var written) != OperationStatus.Done || written != destination.Length)
				throw new FormatException($"Expected {destination.Length * 2} hex characters, got \"{hex}\".");
		}
	}
}
