using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report acg-token-check`: do the ACGHash tokens this server would put on the wire keep retail's
	// one-token-per-template property?
	//
	// `Items.Hash` is the harvested token, 0 when no capture ever showed that template entering an inventory,
	// and `AcgHash.Mint` supplies the rest. Retail keeps one token per template - no template carries two
	// anywhere in the corpus - so a minted token must not land on a harvested one, or on another minted one.
	//
	// It is a report rather than a test, like `--report item-level-check` and `--report npc-pose-check`,
	// because it scores shipped data and fails as that data grows rather than as the code breaks: the collision
	// count is a birthday problem in the item population, and its only sanctioned fix - re-rolling the salt - is a
	// coin flip.
	//
	// Minted tokens all carry a '_' and no harvested token does, so a minted-versus-harvested collision is
	// impossible rather than merely unlikely. What remains is minted versus minted in the '_'-bearing space, a
	// term that shrinks as harvests cover more templates, because each newly harvested one stops being minted for.
	//
	// What stays a test is the part that is a property of the code: minting is deterministic, lands in the charset
	// the client accepts, and never overrides a harvested token (AcgHashTests). A malformed `Items.Hash`
	// likewise stays a test - it can only ever be a bad import, never data that has outgrown the gate.
	//
	// The exit code is non-zero only when a collision actually exists, because that is the half that can only be a
	// defect. The next clean salt is printed alongside so the fix is mechanical rather than a search.
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
			//
			// one term, not two. The minted-vs-harvested term is structurally zero - the two populations are
			// disjoint by charset - so including it would overstate the expectation and make a clean run look lucky
			// when it is guaranteed. What remains is the minted-vs-minted birthday term in the '_'-bearing space.
			var expected = hashless.Count * (double)hashless.Count / (2 * AcgHash.MintSpace);
			Console.WriteLine($"acg-token-check over {items.Count} item(s): {harvested.Count} harvested, {hashless.Count} minted, "
				+ $"{clashes.Count} collision(s) (chance predicts {expected:F2} minted-vs-minted; a salt is clean ~{Math.Exp(-expected):P0} of the time).");

			// The invariant that replaced the lottery, asserted rather than trusted: not one minted token may share the
			// harvested population's charset. A failure here means minting left the '_'-bearing space, which is a code
			// defect and not data outgrowing a gate - the one thing in this report that can only be a defect.
			var strayed = minted.Keys.Count(t => !AcgHash.IsMinted(t));
			if (strayed > 0)
				Console.WriteLine($"  ⚠️ {strayed} minted token(s) carry NO underscore - minting has left the disjoint space and the guarantee is void.");

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
