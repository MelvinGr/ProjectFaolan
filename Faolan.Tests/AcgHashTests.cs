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

		// ⛔ The "no minted token collides with a harvested one" gate USED to live here and is now
		// `--report acg-token-check` (#729's rule). It scored SHIPPED DATA and failed as that data GREW rather than
		// as the code broke: the collision count is a birthday problem in the item population, ~1.5 expected per
		// salt today, so any salt is clean about 22% of the time and every capture harvested makes it worse. Its
		// only sanctioned fix - re-roll AcgHash.Salt - is a coin flip, which is exactly the "weaken the gate"
		// pressure #729 describes. The report prints the count, the expectation it should be compared against, and
		// the next clean salt.
		//
		// What stays here is what is a property of the CODE, plus the one data check whose failure can only ever be
		// a defect rather than growth: a malformed Items.Hash is a bad import, not evidence the gate outgrew.

		// The salted form the `acg-token-check` sweep uses must agree with production at the salt in force - otherwise
		// the salt it recommends is for a different mixer than the one that will run.
		[Fact]
		public void MintWith_TheCurrentSalt_IsExactlyMint()
		{
			for (var id = 0u; id < 5000; id++)
				Assert.Equal(AcgHash.Mint(id), AcgHash.MintWith(id, AcgHash.CurrentSalt));
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
	
		[Fact]
		// ⭐ #899's structural guarantee, and the whole point of the change: every minted token carries an underscore,
		// so it CANNOT equal a harvested one - zero of the 4,615 harvested tokens contain one. This is not a smaller
		// collision chance, it is a disjoint space, and it is what removed the term that grew with every harvest.
		public void EveryMintedToken_CarriesAnUnderscore()
		{
			for (var templateId = 1u; templateId < 5000u; templateId++)
				Assert.True(AcgHash.IsMinted(AcgHash.Mint(templateId)), $"template {templateId} minted {AcgHash.Token(AcgHash.Mint(templateId))}");
		}

		[Fact]
		// ...and it is still a token the client accepts. Its validator (`FUN_005d7190`) walks indices 0..3 applying ONE
		// predicate at every index - 0-9, A-Z or '_' - with no per-position branch, so an underscore is legal in any of
		// the four including the first.
		public void EveryMintedToken_IsStillWellFormed()
		{
			for (var templateId = 1u; templateId < 5000u; templateId++)
				Assert.True(AcgHash.IsWellFormed(AcgHash.Mint(templateId)));
		}

		[Fact]
		// The minting space is used, not collapsed onto one position: all four slots carry the underscore across the
		// population. A mixer that always picked slot 0 would quarter the space and quadruple the residual collisions.
		public void TheUnderscore_LandsInAllFourPositions()
		{
			var slots = new HashSet<int>();
			for (var templateId = 1u; templateId < 5000u; templateId++)
			{
				var token = AcgHash.Mint(templateId);
				for (var i = 0; i < 4; i++)
				{
					if ((char)((token >> (8 * i)) & 0xFF) == '_')
						slots.Add(i);
				}
			}

			Assert.Equal([0, 1, 2, 3], slots.Order());
		}

		[Fact]
		// A HARVESTED token is never treated as minted - the predicate is only sound because the shipped data has no
		// underscore in it, so this pins the direction that would silently break `IsMinted`.
		public void AHarvestedStyleToken_IsNotReportedAsMinted()
		{
			Assert.False(AcgHash.IsMinted(AcgHash.Encode(0)));
			Assert.False(AcgHash.IsMinted(AcgHash.Encode(AcgHash.Space - 1)));
		}
}
}
