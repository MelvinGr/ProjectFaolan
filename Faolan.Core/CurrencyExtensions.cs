using System;
using System.Collections.Generic;
using System.Numerics;

namespace Faolan.Core
{
	public static class CurrencyExtensions
	{
		// Formats an amount of tin (the base wallet unit) as "Ng Ns Nc Nt" (100 tin = 1 copper,
		// 100 copper = 1 silver, 100 silver = 1 gold). By default any zero denomination is omitted
		// (and a zero amount renders as "0t"); pass showAllUnits: true to always emit all four
		// (e.g. "0g 0s 2c 50t"). Generic over any integer width so it works on the server's
		// ulong/uint wallets and the web layer's projections alike (#280).
		public static string ToCurrencyString<T>(this T amount, bool showAllUnits = true) where T : IBinaryInteger<T>
		{
			var tin = Convert.ToUInt64(amount);

			var g = tin / 1_000_000;
			var s = tin / 10_000 % 100;
			var c = tin / 100 % 100;
			var t = tin % 100;

			if (showAllUnits)
				return $"{g}g {s}s {c}c {t}t";

			var parts = new List<string>(4);
			if (g > 0)
				parts.Add($"{g}g");
			if (s > 0)
				parts.Add($"{s}s");
			if (c > 0)
				parts.Add($"{c}c");
			if (t > 0 || parts.Count == 0)
				parts.Add($"{t}t");

			return string.Join(" ", parts);
		}
	}
}
