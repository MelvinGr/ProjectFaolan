using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Faolan.Core.Extensions
{
	public static class StringHexExtensions
	{
		/// <summary>
		///     Parses a hex string into its bytes.
		/// </summary>
		/// <remarks>
		///     Throws on malformed input rather than returning null: a silent null here surfaces later as an NRE or an
		///     empty payload with nothing pointing back at the bad hex.
		/// </remarks>
		/// <exception cref="FormatException">The string is not valid hex.</exception>
		public static byte[] HexToByteArray(this string hex)
		{
			ArgumentNullException.ThrowIfNull(hex);
			return Enumerable.Range(0, hex.Length).Where(x => x % 2 == 0).Select(x => Convert.ToByte(hex.Substring(x, 2), 16)).ToArray();
		}

		public static string ToHex(this IEnumerable<byte> value, bool asArray = false)
		{
			return (asArray
				? "[ "
				: "{ ") + string.Join(", ", value.Select(e => e.ToHex())) + (asArray
				? " ]"
				: " }");
		}

		/// <summary>
		///     The same rendering for a byte window. <see cref="ReadOnlyMemory{T}" /> does not implement
		///     <see cref="IEnumerable{T}" />, so it would otherwise need a <c>ToArray()</c> copy first; the packet
		///     trace holds its header data and body as memory over the frame, so this keeps the hex dump copy-free.
		/// </summary>
		public static string ToHex(this ReadOnlyMemory<byte> value, bool asArray = false)
		{
			var builder = new StringBuilder(asArray
				? "[ "
				: "{ ", value.Length * 6 + 4);

			var span = value.Span;
			for (var i = 0; i < span.Length; i++)
			{
				if (i > 0)
					builder.Append(", ");

				builder.Append(span[i].ToHex());
			}

			return builder
				.Append(asArray
					? " ]"
					: " }")
				.ToString();
		}

		public static string ToHex(this Enum value)
		{
			var enumType = Enum.GetUnderlyingType(value.GetType());

			if (enumType == typeof(byte) || enumType == typeof(sbyte))
				return Convert.ToByte(value).ToHex();

			if (enumType == typeof(ushort) || enumType == typeof(short))
				return Convert.ToUInt16(value).ToHex();

			if (enumType == typeof(uint) || enumType == typeof(int))
				return Convert.ToUInt32(value).ToHex();

			if (enumType == typeof(ulong) || enumType == typeof(long))
				return Convert.ToUInt64(value).ToHex();

			if (enumType == typeof(float))
				return Convert.ToUInt32(value).ToHex();

			if (enumType == typeof(double))
				return Convert.ToUInt64(value).ToHex();

			throw new Exception("Unknown enum type!");
		}

		public static string ToHex(this byte value)
		{
			return $"0x{value:X2}";
		}

		public static string ToHex(this sbyte value)
		{
			return $"0x{value:X2}";
		}

		public static string ToHex(this ushort value)
		{
			return $"0x{value:X4}";
		}

		public static string ToHex(this short value)
		{
			return $"0x{value:X4}";
		}

		public static string ToHex(this uint value)
		{
			return $"0x{value:X8}";
		}

		public static string ToHex(this int value)
		{
			return $"0x{value:X8}";
		}

		public static string ToHex(this ulong value)
		{
			return $"0x{value:X16}";
		}

		public static string ToHex(this long value)
		{
			return $"0x{value:X16}";
		}

		public static string ToHex(this float value)
		{
			return $"0x{value:X8}";
		}

		public static string ToHex(this double value)
		{
			return $"0x{value:X16}";
		}
	}
}
