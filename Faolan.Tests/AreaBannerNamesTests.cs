using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.Core.Crypt;
using Faolan.Core.Rdb.Assets;
using Faolan.Importer.Rdb;
using Xunit;

namespace Faolan.Tests
{
	// The area-banner recovery: the hash that turns a banner id into a client string, and the join that decides
	// which playfield's region a recovered id belongs to.
	//
	// These run with no client RDB - the hash is arithmetic and the join is fed hand-built regions - which is the
	// point: the shipped rows in faolan.db are gated by MapRegionGeometryTests, and this gates the rules that produced
	// them, including the ones that must keep refusing to produce a row.
	public class AreaBannerNamesTests
	{
		// The arithmetic the whole recovery rests on, and the thing that settled a story about the ids being renumbered
		// by a client patch: the banner id is a parameter inside the localized token, and the LDB key is elf28 of the
		// whole token string. 3474586 is the "Castle Approach" banner captured on Tarantia Noble District, and 19303910
		// is the category-50025 id its token hashes to.
		[Theory]
		[InlineData(3474586u, 19303910u)]
		public void ABannerIdsToken_HashesToItsLdbKey(uint bannerId, uint ldbId)
		{
			Assert.Equal(ldbId, Elf28.Of(AreaBannerNames.TokenPrefix + bannerId));
		}

		// The sweep hashes each candidate's digits onto the prefix's state instead of building the string - tens of
		// millions of times, so it matters that it is the same function.
		[Theory]
		[InlineData(0u)]
		[InlineData(7u)]
		[InlineData(3474586u)]
		[InlineData(40_000_000u)]
		public void TheDigitOnlyHash_IsTheSameAsHashingTheWholeToken(uint candidate)
		{
			var whole = Elf28.Of(AreaBannerNames.TokenPrefix + candidate);
			Assert.Equal(whole, Elf28.OfDigits(candidate, Elf28.Of(AreaBannerNames.TokenPrefix)));
		}

		// The three ways the client spells the same place - a widget prefix, run-together words, an apostrophe - all
		// of which are real pairs from the shipped data.
		[Theory]
		[InlineData("BA_TradingPost", "Trading Post")]
		[InlineData("Fifo-CascadeFalls", "Cascade Falls")]
		[InlineData("BA_FIFO_ValleyOfGiants", "Valley of Giants")]
		[InlineData("CrawlingDunes", "Crawling Dunes")]
		[InlineData("Atzels Approach", "Atzel's Approach")]
		[InlineData("Souleaters Pass", "Souleaters' Pass")]
		[InlineData("Mid-steppes", "Mid-steppes")]
		public void ARegionNameAndItsBannerText_NormaliseTogether(string region, string text)
		{
			Assert.Equal(AreaBannerNames.Normalise(text), AreaBannerNames.Normalise(region));
		}

		// The client's other label conventions, all of them real pairs from the shipped data. Ymir's Pass writes its
		// designer bookkeeping into the name field and puts the displayed name in brackets beside it, which is why the
		// join could not see that playfield at all until the parenthetical was read.
		[Theory]
		[InlineData("Camp23 Atzels Bandits (Burned Farms)", "Burned Farms")]
		[InlineData("Camp16 Atzels Bandits (Dolmen)", "The Dolmen")]
		[InlineData("Camps27-28 Stygian Sorcerers (Ruins of Karutonia)", "Ruins of Karutonia")]
		[InlineData("POI A - The Last Cimmerian House", "The Last Cimmerian House")]
		public void ARegionsOtherSpellings_ReachItsBannerText(string region, string text)
		{
			Assert.Contains(AreaBannerNames.Normalise(text), AreaBannerNames.RegionForms(region).Select(AreaBannerNames.Normalise));
		}

		// The possessive has to be offered as an alternative rather than normalised away, because the client is
		// inconsistent about it in both directions: `Ymirs Tongue` keeps the s for "Ymir's Tongue" and `BA_LionMarket`
		// drops it for "The Lion's Market". Collapsing every possessive would fix one and break the other.
		[Theory]
		[InlineData("Ymirs Tongue", "Ymir's Tongue")]
		[InlineData("BA_LionMarket", "The Lion's Market")]
		[InlineData("BA_TraitorCommon", "Traitor's Common")]
		[InlineData("BA_SailorDen", "The Sailor's Den")]
		[InlineData("BA_ScholarsGarden", "Scholar's Garden")]
		public void ARegionName_ReachesItsTextThroughOneOfTheTextsSpellings(string region, string text)
		{
			Assert.Contains(AreaBannerNames.Normalise(region), AreaBannerNames.TextForms(text).Select(AreaBannerNames.Normalise));
		}

		// ...and the pairs it must not join. Normalisation drops punctuation and a widget prefix, never a word: two
		// different places staying apart is what stops the join inventing rows.
		[Theory]
		[InlineData("Scout Camp", "Crossroads")]
		[InlineData("Stream Bridge Camp", "Stream Bridge")]
		[InlineData("Lower Foothills", "Foothills")]
		public void DifferentPlaces_DoNotNormaliseTogether(string region, string text)
		{
			Assert.NotEqual(AreaBannerNames.Normalise(text), AreaBannerNames.Normalise(region));
		}

		// The straightforward case: one recovered id bears the region's name, and a second region on the same
		// playfield anchors the block it sits in.
		[Fact]
		public void ARegionWhoseNameIsRecoveredExactlyOnce_ResolvesWithItsNeighbour()
		{
			var matches = Match(
				[(1080, "Troglodyte Glacier"), (1080, "Aelfrith Camp")],
				[(358073, "Troglodyte Glacier"), (358074, "Aelfrith Camp")],
				captured: []);

			Assert.Equal(2, matches.Count);
			Assert.Equal(358073u, matches.Single(m => m.RegionName == "Troglodyte Glacier").AreaNameId);
			Assert.Equal(358074u, matches.Single(m => m.RegionName == "Aelfrith Camp").AreaNameId);
		}

		// The block gate, which is the whole reason the join is safe. "Outlaw Camp" names a region on 41 playfields and
		// so has 41 recovered ids; only the one inside this playfield's numbering can be its own.
		[Fact]
		public void AGenericName_TakesTheIdInsideThePlayfieldsOwnBlock()
		{
			var matches = Match(
				[(6001, "Trading Village"), (6001, "Lower Foothills"), (6001, "Outlaw Camp")],
				[(4552293, "Trading Village"), (4553229, "Lower Foothills"), (4553228, "Outlaw Camp"), (3882059, "Outlaw Camp"), (4186208, "Outlaw Camp")],
				captured: []);

			Assert.Equal(4553228u, matches.Single(m => m.RegionName == "Outlaw Camp").AreaNameId);
		}

		// A name that matches once in the whole client is still refused when it lands nowhere near the playfield's own
		// numbering. This is map 1010's `Fifo-CimmerianSettlement`: "Cimmerian Settlement" is recovered exactly once,
		// at 711411, while every other banner on that playfield is 37098xx - so the id is some other place of the same
		// name. Uniqueness is not evidence of belonging here.
		[Fact]
		public void AUniqueNameFarFromThePlayfieldsBlock_IsRefused()
		{
			var notes = new List<string>();
			var matches = Match(
				[(1010, "Fifo-CascadeFalls"), (1010, "Fifo-PasstoConarchVillage"), (1010, "Fifo-CimmerianSettlement")],
				[(3709828, "Cascade Falls"), (3709830, "Pass to Conarch Village"), (711411, "Cimmerian Settlement")],
				captured: [],
				notes: notes);

			Assert.DoesNotContain(matches, m => m.RegionName == "Fifo-CimmerianSettlement");
			Assert.Contains(notes, n => n.Contains("Fifo-CimmerianSettlement"));
		}

		// A lone anchor with nothing near it is not a block - unless the wire put it there. A captured id needs no
		// corroboration; a single name match does, or one playfield's chance collision becomes another's banner.
		[Fact]
		public void ALoneNameMatch_IsNotABlock_ButALoneCapturedIdIs()
		{
			var regions = new[] { (2010u, "Castle Approach"), (2010u, "The Mausoleum") };
			var banners = new[] { (3474586u, "Castle Approach"), (3744867u, "Castle Approach"), (3475242u, "The Mausoleum") };

			// Nothing captured: "Castle Approach" is ambiguous, so neither region anchors anything and both are refused.
			Assert.Empty(Match(regions, banners, captured: []));

			// The wire saw 3474586 here, which pins the block - and "The Mausoleum" beside it comes along.
			var withCapture = Match(regions, banners, captured: [(2010u, 3474586u)]);
			Assert.Equal(3474586u, withCapture.Single(m => m.RegionName == "Castle Approach").AreaNameId);
			Assert.Equal(3475242u, withCapture.Single(m => m.RegionName == "The Mausoleum").AreaNameId);
		}

		// Where a captured id is one of several survivors, it wins outright: the block is a proximity argument and the
		// wire is a fact. Old Tarantia's "Outlaw Camp" has 3906077 and 3906603 in block, and retail sent 3906077.
		[Fact]
		public void ACapturedIdAmongTheSurvivors_WinsOutright()
		{
			var matches = Match(
				[(2000, "Outlaw Camp")],
				[(3906077, "Outlaw Camp"), (3906603, "Outlaw Camp")],
				captured: [(2000u, 3906077u)]);

			Assert.Equal(3906077u, Assert.Single(matches).AreaNameId);
		}

		// The guard that keeps a playfield's own numbering authoritative. The same places are named on
		// several playfields - Ymir's Pass's areas exist under 269xxx, 3822xxx and 3908xxx - so a playfield's regions
		// can name-match a whole coherent cluster that belongs to a copy of it. Where the wire has said where this
		// playfield's ids live, only a block holding a captured id counts. Without this, Ymir's Pass imported eight
		// regions off another playfield's block.
		[Fact]
		public void ACoherentBlockThatTheWireContradicts_IsAnotherPlayfieldsCopy()
		{
			var regions = new[] { (1090u, "Wheel of Pain"), (1090u, "Atali Lake"), (1090u, "Frost Gate"), (1090u, "Camp23 Atzels Bandits (Burned Farms)") };
			var banners = new[]
			{
				(3908476u, "Wheel of Pain"), (3908745u, "Atali Lake"), (3908749u, "Frost Gate"), // the copy
				(269299u, "Burned Farms") // this playfield's own block, where the capture is
			};

			// With no capture there is nothing to contradict, so the three-anchor cluster stands.
			Assert.Equal(3, Match(regions, banners, captured: []).Count);

			// With one captured id on this playfield, the far cluster is a copy and only the captured block survives.
			var pinned = Match(regions, banners, captured: [(1090u, 269294u)]);
			Assert.Equal("Camp23 Atzels Bandits (Burned Farms)", Assert.Single(pinned).RegionName);
		}

		// The abbreviation pass - the one rule that is not an exact identity, so it only ever draws from ids already
		// known to be this playfield's, only for regions that matched nothing, and only where the pairing is unique
		// both ways. Old Tarantia's three `AV*` regions against its three "Avenue of X" texts are the case it exists
		// for, and the remainders (Honor/Roses/Lions) are what make each pairing unique.
		[Fact]
		public void AnAbbreviatedRegionName_TakesTheOnlyIdItCanAbbreviate()
		{
			var matches = Match(
				[(2000, "BA_TradingPost"), (2000, "BA_MarketWard"), (2000, "BA_AVRoses"), (2000, "BA_AVLions"), (2000, "BA_AVHonor")],
				[(3373518, "Avenue of Honor"), (3373522, "Trading Post"), (3373523, "Avenue of Roses"), (3373525, "The Market Ward"), (3373530, "Avenue of Lions")],
				captured: []);

			Assert.Equal(3373523u, matches.Single(m => m.RegionName == "BA_AVRoses").AreaNameId);
			Assert.Equal(3373530u, matches.Single(m => m.RegionName == "BA_AVLions").AreaNameId);
			Assert.Equal(3373518u, matches.Single(m => m.RegionName == "BA_AVHonor").AreaNameId);
		}

		// And it refuses rather than picks when the abbreviation fits more than one leftover. `BA_Bridge` abbreviates
		// both "Bridge of Vilerus I" and "Stream Bridge"; on the real data it keeps its geometric match instead.
		[Fact]
		public void AnAmbiguousAbbreviation_IsRefused()
		{
			var notes = new List<string>();
			var matches = Match(
				[(2000, "BA_TradingPost"), (2000, "BA_MarketWard"), (2000, "BA_Bridge")],
				[(3373519, "Bridge of Vilerus I"), (3373522, "Trading Post"), (3373525, "The Market Ward"), (3373526, "Stream Bridge")],
				captured: [],
				notes: notes);

			Assert.DoesNotContain(matches, m => m.RegionName == "BA_Bridge");
			Assert.Contains(notes, n => n.Contains("BA_Bridge") && n.Contains("not unique"));
		}

		// Only the crossing-notifier volumes are candidates. The same place name is also carried by audio and
		// collision regions, and a region that never tells the server the player entered it cannot fire a
		// server-sent banner - so matching one would put the banner on a volume nothing crosses.
		[Fact]
		public void ARegionWithoutTheCrossingNotifierScript_IsNotACandidate()
		{
			var areas = new Dictionary<uint, List<BoundedAreaCollection.BoundedArea>>
			{
				[1080] = [Region("Troglodyte Glacier"), Region("Aelfrith Camp"), Region("Chillcrawler Hill", scryId: 0)]
			};

			var matches = AreaBannerNames.MatchRegions(areas,
				[new AreaBannerNames.Banner(358073, "Troglodyte Glacier"), new AreaBannerNames.Banner(358074, "Aelfrith Camp"), new AreaBannerNames.Banner(358075, "Chillcrawler Hill")],
				new Dictionary<uint, IReadOnlyCollection<uint>>(), []);

			Assert.Equal(2, matches.Count);
			Assert.DoesNotContain(matches, m => m.RegionName == "Chillcrawler Hill");
		}

		private static List<AreaBannerNames.Match> Match(
			IEnumerable<(uint Map, string Name)> regions,
			IEnumerable<(uint Id, string Text)> banners,
			IEnumerable<(uint Map, uint Id)> captured,
			List<string>? notes = null)
		{
			var areas = regions
				.GroupBy(r => r.Map)
				.ToDictionary(g => g.Key, g => g.Select(r => Region(r.Name)).ToList());
			var capturedByMap = captured
				.GroupBy(c => c.Map)
				.ToDictionary(g => g.Key, g => (IReadOnlyCollection<uint>)g.Select(c => c.Id).ToList());

			return AreaBannerNames.MatchRegions(areas, banners.Select(b => new AreaBannerNames.Banner(b.Id, b.Text)).ToList(),
				capturedByMap, notes ?? []);
		}

		// A minimal triangle - the join reads the name and the scryid, never the shape, but a degenerate polygon is
		// excluded upstream so the fixture has to be a real one.
		private static BoundedAreaCollection.BoundedArea Region(string name, uint scryId = BoundedAreaClassifier.CrossingNotifierScryId)
		{
			return new BoundedAreaCollection.BoundedArea(name, [new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f)], 0f, 10f, scryId);
		}
	}
}
