using System.Linq;
using Faolan.Core.Rdb.Assets;
using Xunit;

namespace Faolan.Tests
{
	// The `data/FactionRelations.xml` grammar (RDB 1010701/1, #310). The shipped file is not in this repo - the RDB
	// lives in the client install - so this gates the READER against the exact shapes the shipped file uses, which is
	// what a regression would break. The whole-file numbers are re-derived on every `--report factions` run.
	//
	// The two shapes that matter and are easy to get wrong:
	//   * a relation id is `factionBase + tier`, so `13` is faction 10 tier 3 - NOT faction 13.
	//   * the faction's NAME appears only in the XML comment before its standard entry, and an override's comment
	//     carries the tier too ("Children of Yag-Kosha group boss"), so an override must never overwrite the name.
	public class FactionRelationTableTests
	{
		// The shipped file's exact shape, cut to two factions and three tiers.
		private const string Xml = """
			<?xml version="1.0" encoding="utf-8"?><relations>
			  <!-- Faction points are usually proportional to 60. -->
			  <faction_relations>
			    <!-- Children of Yag-Kosha standard  -->
			    <faction_relation id="10" iconid="4576799">
			      <opposing_faction id="10" points="-120" />
			      <opposing_faction id="20" points="120" />
			    </faction_relation>
			    <!-- Jiang Shi standard -->
			    <faction_relation id="120" iconid="4576791">
			      <opposing_faction id="120" points="-120" />
			    </faction_relation>
			  </faction_relations>
			  <override_relations>
			    <!-- Children of Yag-Kosha solo boss  -->
			    <faction_relation id="11">
			      <opposing_faction id="10" points="-180" />
			      <opposing_faction id="20" points="180" />
			    </faction_relation>
			    <!-- Children of Yag-Kosha raid boss  -->
			    <faction_relation id="15">
			      <opposing_faction id="10" points="-2400" />
			      <opposing_faction id="20" points="2400" />
			    </faction_relation>
			    <!-- Jiang Shi raid boss -->
			    <faction_relation id="125">
			      <opposing_faction id="120" points="-2400" />
			    </faction_relation>
			  </override_relations>
			</relations>
			""";

		[Fact]
		public void Parse_SplitsARelationIdIntoFactionAndTier()
		{
			var table = FactionRelationTable.Parse(Xml);

			Assert.Equal(5, table.Relations.Count);

			// `15` is faction 10 at tier 5, and `125` is faction 120 at tier 5 - a reader that took the id as the
			// faction would produce two factions named 15 and 125 and still parse cleanly.
			var raidBoss = table.Relations.Single(r => r.Id == 15);
			Assert.Equal(10u, raidBoss.Faction);
			Assert.Equal(5, raidBoss.Tier);

			var jiangShiRaid = table.Relations.Single(r => r.Id == 125);
			Assert.Equal(120u, jiangShiRaid.Faction);
			Assert.Equal(5, jiangShiRaid.Tier);
		}

		[Fact]
		public void Parse_TakesTheNameFromTheStandardEntryOnly()
		{
			var table = FactionRelationTable.Parse(Xml);

			// Not "Children of Yag-Kosha raid boss" - the last override's comment must not win.
			Assert.Equal("Children of Yag-Kosha", table.Names[10]);
			Assert.Equal("Jiang Shi", table.Names[120]);
		}

		[Fact]
		public void Parse_ReadsTheRivalPayoutAndTheIcon()
		{
			var table = FactionRelationTable.Parse(Xml);
			var standard = table.Standard.ToList();

			Assert.Equal([10u, 120u], standard.Select(r => r.Faction));
			Assert.Equal(4576799u, standard[0].IconId);

			// Killing one of theirs costs standing with them and earns the same with the rival.
			Assert.Equal(-120, standard[0].Awards.Single(a => a.FactionId == 10).Points);
			Assert.Equal(120, standard[0].Awards.Single(a => a.FactionId == 20).Points);

			// A faction with no rival moves exactly one standing.
			Assert.Equal(120u, Assert.Single(standard[1].Awards).FactionId);
		}

		[Fact]
		public void Tiers_ReturnsOneFactionsLadderInOrder()
		{
			var table = FactionRelationTable.Parse(Xml);

			var ladder = table.Tiers(10).ToList();
			Assert.Equal([0, 1, 5], ladder.Select(r => r.Tier));
			Assert.Equal([-120, -180, -2400], ladder.Select(r => r.Awards.Single(a => a.FactionId == 10).Points));

			// An override carries no icon - only the standard entry does.
			Assert.Equal(0u, ladder[1].IconId);
		}
	}
}
