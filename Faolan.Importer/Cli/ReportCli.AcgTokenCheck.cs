using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report acg-token-check`: do the ACGHash tokens we would put on the wire keep retail's one-token-per-template
	// property? (Gitea #729's rule, applied to `AcgHashTests.MintedTokens_NeverCollideWithAHarvestedOne`.)
	//
	// `Items.Hash` is the HARVESTED token, 0 when no capture ever showed that template entering an inventory;
	// `AcgHash.Mint` supplies the rest. Retail keeps one token per template - 0 templates carrying two across 107,929
	// sightings - so a minted token must not land on a harvested one, or on another minted one.
	//
	// ⭐ **Why it is a REPORT and not a test**, the same argument as `--report item-level-check` and
	// `--report npc-pose-check`: it scores SHIPPED DATA, and it fails as that data GROWS rather than as the code
	// breaks. The collision count is a birthday problem in the item population - with 4,615 harvested and 529 minted
	// tokens in a 36^4 space the expectation is 1.54 per salt, so any given salt is clean about 22% of the time
	// (measured: 90 of 400 consecutive salts). Every capture harvested makes the dominant term bigger. As a test that
	// is a gate which fires on data that is MORE correct than before, and whose only sanctioned fix - re-roll the
	// salt - is a coin flip; run over the real database it is a number to look at and act on.
	//
	// ⚠️ What stays a TEST is the part that is a property of the CODE: minting is deterministic, lands in the charset
	// the client accepts, and never overrides a harvested token (`AcgHashTests`). And `EveryShippedToken_IsWellFormed`
	// stays a test too - a malformed `Items.Hash` can only ever be a bad import, never data that has outgrown the
	// gate, which is the line #729 draws.
	//
	// Exit code: non-zero only when a collision actually exists, because that is the half that can only be a defect.
	// The next clean salt is printed alongside so the fix is mechanical rather than a search.
	public static partial class ReportCli
	{
		// How far to sweep for a clean salt before giving up. At ~22% per salt, 64 is a 1-in-3-million miss.
		private const int AcgSaltSweep = 64;

		private static async Task<int> AcgTokenCheck()
		{
			await using var db = OpenDb();
			var items = await db.Items.AsNoTracking().Select(i => new { i.Id, i.Hash, i.Name }).ToListAsync();

			var harvested = items.Where(i => i.Hash != 0).ToDictionary(i => i.Hash, i => i.Name);
			var hashless = items.Where(i => i.Hash == 0).ToList();

			var minted = new Dictionary<uint, string>();
			var clashes = new List<string>();
			foreach (var item in hashless)
			{
				var token = AcgHash.Mint(item.Id);
				if (harvested.TryGetValue(token, out var owner))
					clashes.Add($"  '{item.Name}' ({item.Id}) mints {AcgHash.Token(token)}, already harvested for '{owner}'");
				else if (!minted.TryAdd(token, item.Name))
					clashes.Add($"  '{item.Name}' ({item.Id}) mints {AcgHash.Token(token)}, already minted for '{minted[token]}'");
			}

			// The expectation this population produces, so the count below reads as "about what chance predicts" or
			// "something is wrong with the mixer" rather than as a bare number.
			var space = (double)AcgHash.Space;
			var expected = hashless.Count * harvested.Count / space + hashless.Count * (double)hashless.Count / (2 * space);
			Console.WriteLine($"acg-token-check over {items.Count} item(s): {harvested.Count} harvested, {hashless.Count} minted, "
				+ $"{clashes.Count} collision(s) (chance predicts {expected:F2}; a salt is clean ~{Math.Exp(-expected):P0} of the time).");

			if (clashes.Count == 0)
				return 0;

			Console.WriteLine(string.Join(Environment.NewLine, clashes));
			Console.WriteLine();

			var clean = FindCleanSalt(hashless.Select(i => i.Id).ToList(), harvested.Keys.ToHashSet());
			Console.WriteLine(clean is { } salt
				? $"Set AcgHash.Salt = 0x{salt:X8} (the first clean value at or after the current one)."
				: $"No clean salt in the next {AcgSaltSweep}. That is a 1-in-millions miss at this population - suspect the mixer, not luck.");

			return 1;
		}

		// Re-runs the mint with candidate salts and returns the first that collides with nothing. It reproduces
		// AcgHash.Mint rather than calling it because the salt is a compile-time constant there - deliberately, since
		// a settable one would let two processes mint different tokens for the same template.
		private static uint? FindCleanSalt(IReadOnlyList<uint> hashless, IReadOnlySet<uint> harvested)
		{
			for (var i = 0u; i < AcgSaltSweep; i++)
			{
				var salt = AcgHash.CurrentSalt + i;
				var seen = new HashSet<uint>();
				if (hashless.All(id => AcgHash.MintWith(id, salt) is var token && !harvested.Contains(token) && seen.Add(token)))
					return salt;
			}

			return null;
		}
	}
}
