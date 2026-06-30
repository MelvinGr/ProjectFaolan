using System.Collections.Generic;
using System.Numerics;

namespace Faolan.Core
{
	public static class CurrencyExtensions
	{
		// Formats an amount of tin (the base wallet unit) as "Ng Ns Nc Nt", omitting any zero
		// denomination (100 tin = 1 copper, 100 copper = 1 silver, 100 silver = 1 gold). A zero
		// amount renders as "0t". Generic over any integer width so it works on the server's
		// ulong/uint wallets and the web layer's projections alike (#280).
		public static string ToCurrencyString<T>(this T tin) where T : IBinaryInteger<T>
		{
			var hundred = T.CreateChecked(100);
			var (g, s, c, t) = (tin / T.CreateChecked(1_000_000), tin / T.CreateChecked(10_000) % hundred, tin / hundred % hundred, tin % hundred);

			var parts = new List<string>(4);
			if (g > T.Zero)
				parts.Add($"{g}g");
			if (s > T.Zero)
				parts.Add($"{s}s");
			if (c > T.Zero)
				parts.Add($"{c}c");
			if (t > T.Zero || parts.Count == 0)
				parts.Add($"{t}t");
			return string.Join(" ", parts);
		}
	}
}
