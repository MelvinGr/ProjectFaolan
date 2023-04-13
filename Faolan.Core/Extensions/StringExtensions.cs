using System.Globalization;
using System.Text;

namespace Faolan.Core.Extensions
{
	public static class StringExtensions
	{
		public static string ToTitleCase(this string str, bool force = true)
		{
			return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(force ? str.ToLower() : str);
		}

		public static string PadBoth(this string str, int totalLength, char c = ' ')
		{
			var spaces = totalLength - str.Length;
			var padLeft = spaces / 2 + str.Length;
			return str.PadLeft(padLeft, c).PadRight(totalLength, c);
		}

		public static bool TryParseNormalOrHex(this string input, out byte output)
		{
			return input.StartsWith("0x") || input.StartsWith("0X") || input.StartsWith("#")
				? byte.TryParse(input.Substring(input[0] == '#' ? 1 : 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out output)
				: byte.TryParse(input, out output);
		}

		public static bool TryParseNormalOrHex(this string input, out ushort output)
		{
			return input.StartsWith("0x") || input.StartsWith("0X") || input.StartsWith("#")
				? ushort.TryParse(input.Substring(input[0] == '#' ? 1 : 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out output)
				: ushort.TryParse(input, out output);
		}

		public static bool TryParseNormalOrHex(this string input, out uint output)
		{
			return input.StartsWith("0x") || input.StartsWith("0X") || input.StartsWith("#")
				? uint.TryParse(input.Substring(input[0] == '#' ? 1 : 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out output)
				: uint.TryParse(input, out output);
		}

		public static bool TryParseNormalOrHex(this string input, out ulong output)
		{
			return input.StartsWith("0x") || input.StartsWith("0X") || input.StartsWith("#")
				? ulong.TryParse(input.Substring(input[0] == '#' ? 1 : 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out output)
				: ulong.TryParse(input, out output);
		}

		public static string GetPrintable(this byte[] bytes)
		{
			var builder = new StringBuilder();
			foreach (var b in bytes)
			{
				if (b >= 0x21 && b <= 0x7e)
					builder.Append((char)b);
				else if (b == '\n')
					builder.Append(' ');
			}

			return builder.ToString();
		}
	}
}
