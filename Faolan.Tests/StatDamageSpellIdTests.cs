using System;
using System.Collections.Generic;
using System.IO;
using Faolan.GameServer.Combat;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// ⛔ #767/#770 got this BACKWARDS. They measured `StatDamageIIR.SpellBaseId` over the stat ids 0, 5 and 10 - where
	// 3,059 of 3,061 records really do carry 0 - and applied the finding to the HEALTH emitters too. Re-measured over
	// statId 0x1B (27), retail names a REAL spell in 478 of 656 own-handle records and 1,698 of 1,724 on other
	// handles; the zeroes are school 12, the world. So a health record states a spell, because retail states one.
	//
	// ⚠️ It is NOT because a zero would void the record. #902 read it that way from a client log and the disassembly
	// refutes it: the apply (`StatDamageIIR_t` slot 2, `FUN_008d5960`) reads `spellBaseId` only inside
	// `if (deathReason == 10)`, and the `SetStat(statId, GetStat(statId) + delta)` above it is unconditional. The
	// frozen health bar was the DELTA'S SCALE, fixed in `CombatService.SendStatDamage`. Keep the two apart: this gate
	// is about matching retail, not about a client that would otherwise reject us.
	//
	// ⚠️ #767's own diagnosis was right and only its fix was wrong. The original defect was a DOUBLE SHIFT - a value
	// that was already a base id shifted again into `14321` - and the client said exactly the same thing about that
	// (`failed to load spell with id 14321`). Removing the id turned a wrong spell into no spell and kept the error.
	// The rule is "state the real base id", and the shift is what varies: `buff.BuffId` is a WIRE id and needs `>> 8`,
	// a cast trailer's `spellId` is already a base and must not be shifted.
	//
	// This is a source scan rather than a wire assertion, and that is deliberate: the failure it exists to catch is not
	// a wrong byte, it is a SECOND emitter drifting away from the first. #767 fixed the spell-cast path and left the DoT
	// tick passing `buff.BuffId >> 8`, which survived review for exactly the reason that makes this hard to notice -
	// that value IS a real spell id, so the client loads it without complaint and nothing looks wrong in-game. A test
	// that checked one call site would have kept passing.
	public class StatDamageSpellIdTests
	{
		[Fact]
		public void NoSendStatDamage_ZeroesTheSpellIdOnAHealthRecord()
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
					// Every SendStatDamage in the tree is a HEALTH record (it reports `vitals.Health`), so none of them
					// may state the zero the 0/5/10 measurement licensed.
					if (!call.Contains(nameof(CombatResolver.RetailSpellBaseId), StringComparison.Ordinal))
						continue;

					offenders.Add($"{name}: {Collapse(call)}");
				}
			}

			Assert.True(offenders.Count == 0,
				$"A StatDamageIIR carrying HEALTH must name a real spell base id. {nameof(CombatResolver.RetailSpellBaseId)} (0) is measured for the stat "
				+ "ids 0/5/10 only; a health record states the creature's or the caster's real ability, as retail does:\n  "
				+ string.Join("\n  ", offenders));
			// The four emitters: the spell-cast hit, the DoT tick, and the NPC's swing and cast. A scan that found
			// none would pass vacuously.
			Assert.True(checkedCount >= 4, $"only {checkedCount} SendStatDamage call site(s) found - the scan is probably broken");
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
