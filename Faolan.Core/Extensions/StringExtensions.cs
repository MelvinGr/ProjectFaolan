using System;
using System.Globalization;

namespace Faolan.Core.Extensions
{
	public static class StringExtensions
	{
		extension(string str)
		{
			public byte[] Base64Decode()
			{
				return string.IsNullOrWhiteSpace(str)
					? []
					: Convert.FromBase64String(str);
			}

			public bool ContainsIgnoreCase(string find)
			{
				return str.Contains(find, StringComparison.InvariantCultureIgnoreCase);
			}

			public string PadBoth(int totalLength, char c = ' ')
			{
				var spaces = totalLength - str.Length;
				var padLeft = spaces / 2 + str.Length;
				return str.PadLeft(padLeft, c).PadRight(totalLength, c);
			}

			public T To<T>(IFormatProvider? provider = null) where T : IParsable<T>
			{
				return T.Parse(str, provider);
			}

			public string ToTitleCase(bool force = true)
			{
				return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(force
					? str.ToLower()
					: str);
			}

			public bool TryTo<T>(out T result, IFormatProvider? provider = null) where T : IParsable<T>
			{
				if (T.TryParse(str, provider, out result!))
					return true;

				result = default!;
				return false;
			}
		}
	}
}
