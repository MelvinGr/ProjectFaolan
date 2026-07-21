using Faolan.Core.Network.Codecs.Guild;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Locks the 0x205C DispatchGuildSearchResult codec (the guild-finder browse list) byte-exact against the
	// captured retail packet (50 guilds) and checks a built list round-trips.
	public class GuildSearchResultTests
	{
		[Fact]
		public void Build_RoundTrips()
		{
			var built = new GuildSearchResult
			{
				Guilds = [new GuildSearchResult.Entry(1, "Faolan", "NecroL80", 3, 0, 0), new GuildSearchResult.Entry(2, "Second", "Leader", 12, 4500, 1)]
			};

			var round = GuildSearchResult.Decode(built.Encode());
			Assert.Equal(2, round.Guilds.Count);
			Assert.Equal("Faolan", round.Guilds[0].Name);
			Assert.Equal("NecroL80", round.Guilds[0].LeaderName);
			Assert.Equal(4500u, round.Guilds[1].Renown);
		}

		[Fact]
		public void Decode_ParsesGuildRows()
		{
			var r = GuildSearchResult.Decode(Golden.Bytes("GuildSearchResult"));

			Assert.Equal(50, r.Guilds.Count);
			Assert.Equal("Legacy of Crom [Legacy of Deathwhisper]", r.Guilds[0].Name);
			Assert.Equal("Rahkhan", r.Guilds[0].LeaderName);
			Assert.Equal(28, r.Guilds[0].MemberCount);
		}

		[Fact]
		public void Decode_ReEncode_IsByteIdentical()
		{
			var golden = Golden.Bytes("GuildSearchResult");
			Assert.Equal(golden, GuildSearchResult.Decode(golden).Encode());
		}
	}
}
