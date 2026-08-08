using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Faolan.Core.Database;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// The ACGHash token space (#723). Items.Hash is the HARVESTED token - 0 when no capture ever showed the template
	// entering an inventory - and AcgHash.ForWire mints one for the rest, because the client carries the token
	// opaquely: it validates the four characters and resolves nothing through them.
	public class AcgHashTests
	{
		private readonly ITestOutputHelper _o;

		public AcgHashTests(ITestOutputHelper o)
		{
			_o = o;
		}

		// The property that makes minting safe in the first place: the client's reader accepts [0-9A-Z_] and
		// rejects everything else, so a token built any other way would be refused for its BYTES rather than for
		// its identity - which is the false negative that nearly killed this investigation.
		[Fact]
		public void EveryMintedToken_IsInTheCharsetTheClientAccepts()
		{
			for (var id = 0u; id < 20000; id++)
			{
				var minted = AcgHash.Mint(id);
				Assert.True(AcgHash.IsWellFormed(minted), $"template {id} minted 0x{minted:X8} ({AcgHash.Token(minted)}), which is not four [0-9A-Z_] bytes");
			}
		}

		[Fact]
		public void Minting_IsDeterministic()
		{
			Assert.Equal(AcgHash.Mint(163116), AcgHash.Mint(163116));
			Assert.NotEqual(AcgHash.Mint(163116), AcgHash.Mint(163118));
		}

		// A harvested token always wins - minting is the fallback for an ABSENT one, never an override.
		[Fact]
		public void ForWire_PrefersTheHarvestedToken()
		{
			Assert.Equal(909133648u, AcgHash.ForWire(163118, 909133648)); // Feywater, '60GP'
			Assert.Equal(AcgHash.Mint(163116), AcgHash.ForWire(163116, 0));
		}

		[Fact]
		public void Encode_RoundTripsThroughTheTokenText()
		{
			Assert.Equal("0000", AcgHash.Token(AcgHash.Encode(0)));
			Assert.Equal("ZZZZ", AcgHash.Token(AcgHash.Encode(AcgHash.Space - 1)));
			Assert.Equal("60GP", AcgHash.Token(909133648)); // the harvested Feywater token, as text
		}

		// Retail keeps one token per template: 3,993 harvested values, 3,993 distinct, and 0 templates carrying two
		// across 107,929 sightings in the capture dumps. A minted token that duplicated a harvested one would break
		// that property, so the gate holds it - bump AcgHash.Salt if it ever fires.
		[Fact]
		public async Task MintedTokens_NeverCollideWithAHarvestedOne()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var items = await db.Items.AsNoTracking().Select(i => new { i.Id, i.Hash, i.Name }).ToListAsync(TestContext.Current.CancellationToken);

			var harvested = items.Where(i => i.Hash != 0).ToDictionary(i => i.Hash, i => i.Name);
			var hashless = items.Where(i => i.Hash == 0).ToList();
			_o.WriteLine($"{items.Count} items: {harvested.Count} harvested, {hashless.Count} minted");

			var minted = new Dictionary<uint, string>();
			var clashes = new List<string>();
			foreach (var item in hashless)
			{
				var token = AcgHash.Mint(item.Id);
				if (harvested.TryGetValue(token, out var owner))
					clashes.Add($"  '{item.Name}' ({item.Id}) minted {AcgHash.Token(token)}, already harvested for '{owner}'");
				else if (!minted.TryAdd(token, item.Name))
					clashes.Add($"  '{item.Name}' ({item.Id}) minted {AcgHash.Token(token)}, already minted for '{minted[token]}'");
			}

			Assert.True(clashes.Count == 0, $"{clashes.Count} minted token(s) collide - bump AcgHash.Salt:\n" + string.Join("\n", clashes));
		}

		// Every shipped token is one the client would accept. This covers the HARVESTED half too, so a bad import
		// (a misread field landing in Hash) fails here rather than on the wire.
		[Fact]
		public async Task EveryShippedToken_IsWellFormed()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var bad = await db.Items.AsNoTracking().Where(i => i.Hash != 0).Select(i => new { i.Id, i.Hash, i.Name }).ToListAsync(TestContext.Current.CancellationToken);

			var offenders = bad.Where(i => !AcgHash.IsWellFormed(i.Hash)).ToList();
			Assert.True(offenders.Count == 0,
				"Items.Hash must be a 4-character [0-9A-Z_] ACGHash the client's reader accepts:\n  "
				+ string.Join("\n  ", offenders.Select(o => $"item {o.Id} '{o.Name}' Hash 0x{o.Hash:X8}")));
		}
	}
}
