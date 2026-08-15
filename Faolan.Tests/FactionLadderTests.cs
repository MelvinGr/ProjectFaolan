using Faolan.Core.Database;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Faction;
using Faolan.Core.Progression;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using Xunit;

namespace Faolan.Tests
{
	// The Khitai reputation ladder and its two wire messages (#310).
	//
	// ⚠️ There is NO capture oracle for any of this - the corpus carries zero faction packets in either direction.
	// So these gates assert two things that do not need one: that the ladder reproduces the CLIENT's own tuning-sheet
	// numbers, and that the codecs round-trip. What they deliberately do NOT assert is a golden byte string, because
	// there is no retail body to compare one against and a self-authored golden would only be testing our encoder
	// against itself.
	public class FactionLadderTests
	{
		// The tuning sheet's own values (RDB 1000516). Restated here rather than referenced, so a change to the
		// constants has to be made in two places and cannot be a slip.
		[Theory]
		[InlineData(0, 0)]
		[InlineData(49_999, 0)]
		[InlineData(50_000, 1)]
		[InlineData(199_999, 1)]
		[InlineData(200_000, 2)]
		[InlineData(449_999, 2)]
		[InlineData(450_000, 3)]
		[InlineData(999_999, 3)]
		[InlineData(1_000_000, 4)]
		[InlineData(1_100_000, 4)]
		public void RankFor_IsTheTuningSheetsLadder(int points, int expected)
		{
			Assert.Equal(expected, FactionLadder.RankFor(points));
		}

		// ⭐ The whole negative half is rank 0 - a hostile standing has no rank, it has a hostile standing. This is the
		// half nothing but the tuning sheet stated, and the half the rival-pair rule needs.
		[Theory]
		[InlineData(-1)]
		[InlineData(-5_000)]
		[InlineData(-50_000)]
		[InlineData(-1_100_000)]
		public void EveryNegativeStandingIsRankZero(int points)
		{
			Assert.Equal(0, FactionLadder.RankFor(points));
		}

		[Theory]
		[InlineData(0, true, false)]
		[InlineData(5_000, true, false)]
		[InlineData(5_001, false, false)]
		[InlineData(-5_000, true, false)]
		[InlineData(-5_001, false, true)]
		public void TheNeutralBandIsSymmetricAboutZero(int points, bool neutral, bool hostile)
		{
			Assert.Equal(neutral, FactionLadder.IsNeutral(points));
			Assert.Equal(hostile, FactionLadder.IsHostile(points));
		}

		// The clamp is the reason `Apply` exists rather than a `+` at the call site: an unclamped grind would run a
		// standing past a ceiling the client draws its bar against.
		[Fact]
		public void ApplyClampsToTheClientsBounds()
		{
			Assert.Equal(FactionLadder.MaxPoints, FactionLadder.Apply(FactionLadder.MaxPoints - 10, 1_000));
			Assert.Equal(FactionLadder.MinPoints, FactionLadder.Apply(FactionLadder.MinPoints + 10, -1_000));
			Assert.Equal(120, FactionLadder.Apply(0, 120));
			Assert.Equal(-120, FactionLadder.Apply(0, -120));
		}

		// ⭐ The corroboration the decode rests on: the payout matrix and the thresholds belong to the same system.
		// At the shipped 120 points for a solo normal kill, rank 1 is ~417 kills and rank 4 ~8,333 - the order of
		// magnitude Khitai reputation is remembered for. A threshold set belonging to something else would have no
		// reason to land there.
		[Fact]
		public void TheThresholdsAndTheSoloPayoutAgree()
		{
			const int soloNormal = 120;
			Assert.InRange(FactionLadder.Rank1 / soloNormal, 400, 450);
			Assert.InRange(FactionLadder.Rank4 / soloNormal, 8_000, 8_500);
		}

		[Theory]
		[InlineData(10u, 0)]
		[InlineData(90u, 1_100_000)]
		[InlineData(330u, -1_100_000)] // the SIGNED half, which a uint wire slot has to carry as two's complement
		public void ApplyFactionPoints_RoundTrips(uint factionId, int points)
		{
			var built = ApplyFactionPointsCodec.Build(factionId, points);
			var read = ApplyFactionPointsCodec.Parse(built);

			Assert.Equal(factionId, read.FactionId);
			Assert.Equal(points, read.Points);
			Assert.Equal(8, built.Length); // two bare u32, no header of their own - #132's rule for a scalar method
			Assert.Equal(built, ApplyFactionPointsCodec.Build(read));
		}

		[Theory]
		[InlineData(10u, 0u)]
		[InlineData(90u, 4u)]
		public void ApplyFactionRank_RoundTrips(uint factionId, uint rank)
		{
			var built = ApplyFactionRankCodec.Build(factionId, rank);
			var read = ApplyFactionRankCodec.Parse(built);

			Assert.Equal(factionId, read.FactionId);
			Assert.Equal(rank, read.Rank);
			Assert.Equal(8, built.Length);
			Assert.Equal(built, ApplyFactionRankCodec.Build(read));
		}

		[Fact]
		public void Parse_RejectsAShortOrOverlongBody()
		{
			Assert.Throws<CodecParseException>(() => ApplyFactionRankCodec.Parse(new byte[7]));
			Assert.Throws<CodecParseException>(() => ApplyFactionRankCodec.Parse(new byte[9]));
		}

		// The shipped catalog, against the client's own file. Not a restatement of the importer: these are the
		// structural facts docs/subsystems/factions.md records, and each one would break differently if the ordered
		// LDB runs were joined to the XML by POSITION instead of by name.
		[Fact]
		public void TheShippedCatalogMatchesTheClientsFile()
		{
			using var ctx = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var factions = ctx.Factions.AsNoTracking().OrderBy(f => f.Id).ToList();

			Assert.Equal(16, factions.Count);

			// Exactly nine carry a player rank ladder, and they are ids 10..90 - the Khitai factions a player joins.
			var ranked = factions.Where(f => f.HasPlayerRanks).Select(f => f.Id).ToList();
			Assert.Equal<uint[]>([10, 20, 30, 40, 50, 60, 70, 80, 90], [.. ranked]);

			// The six rival PAIRS are symmetric in the file...
			foreach (var f in factions.Where(f => f.RivalFactionId is not null && f.Id <= 90))
			{
				var rival = factions.Single(r => r.Id == f.RivalFactionId);
				Assert.Equal(f.Id, rival.RivalFactionId);
			}

			// ...and 100 is the documented asymmetry: it names 90 as its rival yet has no ladder of its own, so the
			// Wolves' rivalry is one-sided from the PLAYER's side. Pinned because it reads like a bug and is not.
			var hyrkanians = factions.Single(f => f.Id == 100);
			Assert.Equal(90u, hyrkanians.RivalFactionId);
			Assert.False(hyrkanians.HasPlayerRanks);

			// A name joined from the LDB rather than left as the XML's authoring comment.
			Assert.Equal("Wolves of the Steppes", factions.Single(f => f.Id == 90).Name);
		}

		// The payout matrix: six tiers, and the file's own "proportional to 60" design rule.
		[Fact]
		public void TheKillPayoutMatrixCarriesEveryTier()
		{
			using var ctx = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var awards = ctx.FactionKillAwards.AsNoTracking().ToList();

			Assert.NotEmpty(awards);
			Assert.Equal<int[]>([0, 1, 2, 3, 4, 5], [.. awards.Select(a => a.Tier).Distinct().Order()]);

			// ⭐ Killing one of a faction's own NPCs COSTS standing with them: every tier-0 self-award is negative.
			foreach (var self in awards.Where(a => a.Tier == 0 && a.FactionId == a.VictimFactionId))
				Assert.True(self.Points < 0, $"faction {self.VictimFactionId} pays {self.Points} to itself for a kill");

			// The shipped solo-normal rate, which the ladder gate above is calibrated against.
			var soloNormal = awards.Where(a => a.Tier == 0 && a.Points > 0).Select(a => a.Points).Distinct().ToList();
			Assert.Equal([120], soloNormal);
		}
	}
}
