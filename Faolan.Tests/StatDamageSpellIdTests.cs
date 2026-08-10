using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Faolan.GameServer.Combat;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// #767/#770: retail puts NOTHING in `StatDamageIIR.SpellBaseId` - 3,059 of the corpus' 3,061 records carry 0, and
	// the two that do not name an id absent from the spell catalog.
	//
	// This is a source scan rather than a wire assertion, and that is deliberate: the failure it exists to catch is not
	// a wrong byte, it is a SECOND emitter drifting away from the first. #767 fixed the spell-cast path and left the DoT
	// tick passing `buff.BuffId >> 8`, which survived review for exactly the reason that makes this hard to notice -
	// that value IS a real spell id, so the client loads it without complaint and nothing looks wrong in-game. A test
	// that checked one call site would have kept passing.
	public class StatDamageSpellIdTests
	{
		[Fact]
		public void EverySendStatDamage_PassesTheMeasuredRetailSpellBaseId()
		{
			var offenders = new List<string>();
			var checkedCount = 0;
			foreach (var file in Directory.EnumerateFiles(TestPaths.Repo("Faolan"), "*.cs", SearchOption.AllDirectories))
			{
				var name = Path.GetFileName(file);
				foreach (var call in Calls(File.ReadAllText(file), "SendStatDamage("))
				{
					// The interface declaration and the listener's forwarder pass the parameter THROUGH; only a site that
					// decides the value is interesting.
					if (call.Contains("uint spellBaseId", StringComparison.Ordinal) || call.TrimEnd().EndsWith("spellBaseId", StringComparison.Ordinal))
						continue;

					checkedCount++;
					if (call.Contains(nameof(CombatResolver.RetailSpellBaseId), StringComparison.Ordinal))
						continue;

					offenders.Add($"{name}: {Collapse(call)}");
				}
			}

			Assert.True(offenders.Count == 0,
				$"Every StatDamageIIR must state retail's own {nameof(CombatResolver.RetailSpellBaseId)} (0), not a spell id the client will happily load:\n  "
				+ string.Join("\n  ", offenders));
			// Both emitters: the spell-cast hit and the DoT tick. A scan that found neither would pass vacuously.
			Assert.True(checkedCount >= 2, $"only {checkedCount} SendStatDamage call site(s) found - the scan is probably broken");
		}

		// The argument list of each `<marker>` call, depth-counted so a nested call's parens do not end it early.
		private static IEnumerable<string> Calls(string source, string marker)
		{
			for (var at = source.IndexOf(marker, StringComparison.Ordinal); at >= 0; at = source.IndexOf(marker, at + 1, StringComparison.Ordinal))
			{
				var start = at + marker.Length;
				var depth = 1;
				var i = start;
				for (; i < source.Length && depth > 0; i++)
				{
					if (source[i] == '(')
						depth++;
					else if (source[i] == ')')
						depth--;
				}

				if (depth == 0)
					yield return source[start..(i - 1)];
			}
		}

		private static string Collapse(string text)
		{
			return string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
		}
	}
}
