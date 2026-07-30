using System.Linq;
using Faolan.Core.Network.Codecs.Guild;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Locks the 0x2038 DispatchGuildRankingsUpdate codec (the guild-renown leaderboard) byte-exact against the
	// captured retail packet, and checks the DB builder produces a well-formed body the same decoder round-trips.
	public class GuildRankingsCodecTests
	{
		[Fact]
		public void Decode_ParsesRecordsAndSections()
		{
			var r = GuildRankingsCodec.Parse(Golden.Bytes("GuildRankings"));

			Assert.Equal(48, r.Records.Count);
			Assert.All(r.Sections, s => Assert.Equal(20, s.Count)); // retail lists the top 20 per tab

			// The "Total" column is the sum of the three categories for every captured guild.
			Assert.All(r.Records, row => Assert.Equal(row.Valor + row.Glory + row.Artistry, row.Total));

			// Each section is ordered by that category's renown descending (the current rank is the entry position).
			foreach (var section in r.Sections)
				Assert.Equal(section.Select(e => e.Renown).OrderByDescending(x => x), section.Select(e => e.Renown));
		}

		// The 4773-byte guild-rankings download from the hoxxling guild_invite_accepted capture (48 guilds, 4 tabs).
		[Fact]
		public void Decode_ReEncode_IsByteIdentical()
		{
			var golden = Golden.Bytes("GuildRankings");
			Assert.Equal(golden, GuildRankingsCodec.Build(GuildRankingsCodec.Parse(golden)));
		}

		[Fact]
		public void FromGuilds_RanksByCategoryAndRoundTrips()
		{
			// Total = V+G+A: Alpha 30, Bravo 60, Charlie 0 (fresh guild - absent from every tab).
			var built = GuildRankings.FromGuilds([
				new GuildRankings.Row(1, "Alpha", 5, 10, 15, 3, 4), new GuildRankings.Row(2, "Bravo", 40, 20, 0, 9, 7), new GuildRankings.Row(3, "Charlie", 0, 0, 0, 1, 1)
			]);

			// Records are sorted by total desc: Bravo(60), Alpha(30), Charlie(0).
			Assert.Equal(["Bravo", "Alpha", "Charlie"], built.Records.Select(r => r.Name));

			// Total tab: Bravo(60) at position 0, Alpha(30) at position 1 - Charlie (0) excluded.
			Assert.Equal([(2u, 60ul, 0u), (1u, 30ul, 1u)], built.Sections[0].Select(e => (e.Id, e.Renown, e.PrevRank)));
			// Artistry tab: only Alpha has any (15).
			Assert.Equal([(1u, 15ul, 0u)], built.Sections[3].Select(e => (e.Id, e.Renown, e.PrevRank)));

			// The builder's body decodes back to an equal structure.
			var round = GuildRankingsCodec.Parse(GuildRankingsCodec.Build(built));
			Assert.Equal(built.Records.Select(r => r.Id), round.Records.Select(r => r.Id));
			Assert.Equal(built.Sections[0].Count, round.Sections[0].Count);
		}
	}
}
