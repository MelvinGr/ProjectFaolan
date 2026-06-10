using System;
using System.Globalization;
using System.Text;

namespace Faolan.Core.Extensions
{
	public static class StringExtensions
	{
		extension(string str)
		{
			public byte[] Base64Decode()
			{
				if (string.IsNullOrWhiteSpace(str))
					return [];

				return Convert.FromBase64String(str);
			}

			public bool ContainsIgnoreCase(string find)
			{
				return str.Contains(find, StringComparison.InvariantCultureIgnoreCase);
			}

			public string PadBoth(int totalLength, char c = ' ')
			{
				var spaces = totalLength - str.Length;
				var padLeft = spaces / 2 + str.Length;
				return str
					.PadLeft(padLeft, c)
					.PadRight(totalLength, c);
			}

			public string ToTitleCase(bool force = true)
			{
				return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(force
					? str.ToLower()
					: str);
			}

			public bool TryParseNormalOrHex(out byte output)
			{
				return str.StartsWith("0x") || str.StartsWith("0X") || str.StartsWith("#")
					? byte.TryParse(str.Substring(str[0] == '#'
						? 1
						: 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out output)
					: byte.TryParse(str, out output);
			}

			public bool TryParseNormalOrHex(out ushort output)
			{
				return str.StartsWith("0x") || str.StartsWith("0X") || str.StartsWith("#")
					? ushort.TryParse(str.Substring(str[0] == '#'
						? 1
						: 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out output)
					: ushort.TryParse(str, out output);
			}

			public bool TryParseNormalOrHex(out uint output)
			{
				return str.StartsWith("0x") || str.StartsWith("0X") || str.StartsWith("#")
					? uint.TryParse(str.Substring(str[0] == '#'
						? 1
						: 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out output)
					: uint.TryParse(str, out output);
			}

			public bool TryParseNormalOrHex(out ulong output)
			{
				return str.StartsWith("0x") || str.StartsWith("0X") || str.StartsWith("#")
					? ulong.TryParse(str.Substring(str[0] == '#'
						? 1
						: 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out output)
					: ulong.TryParse(str, out output);
			}
		}

		extension(byte[] bytes)
		{
			public string GetPrintable()
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

			public string ToAsciiRepresentation(char nonPrintable = '.')
			{
				var builder = new StringBuilder(bytes.Length);
				foreach (var b in bytes)
				{
					builder.Append(b >= 0x20 && b <= 0x7e
						? (char)b
						: nonPrintable);
				}

				return builder.ToString();
			}
		}
	}
}
