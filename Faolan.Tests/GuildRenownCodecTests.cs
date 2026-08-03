using System;
using Faolan.Core.Network.Codecs.Guild;
using Xunit;

namespace Faolan.Tests
{
	// #104's last two undecoded guild messages, both off the CSPlayerAgent tier and both long carried as "replayed
	// blob" / "semantics unknown". The bodies here are retail's, verbatim from the captures named on each one.
	public class GuildRenownCodecTests
	{
		// 0x2039 DispatchGuildRenownHistoryUpdate, Hoxxling_mage-heraldofxotli_login. Four 28-byte entries; every u64
		// carries a zero high word, which is what identifies the widths.
		private const string CapturedHistory =
			"0000000000000225000000000008a98c0000000000000627000000ad" +
			"00000000000000080000000000" + "2ed3af0000000000001e24000000aa" +
			"0000000000002bdc00000000002a4c74000000000004dd9c00000080" +
			"000000000000040a000000000002e0390000000000002c520000006d";

		// The same message from the SAME guild one login later (…_login_2): only the first entry has moved.
		private const string CapturedHistorySameGuild =
			"00000000000004c200000000000e7adb0000000000001761000000af" +
			"000000000000000800000000002ed3af0000000000001e24000000aa" +
			"0000000000002bdc00000000002a4c74000000000004dd9c00000080" +
			"000000000000040a000000000002e0390000000000002c520000006d";

		// The corpus's OTHER guild (…_login_gameplay): three of the four entries move, and the third does not.
		private const string CapturedHistoryOtherGuild =
			"000000000000000600000000002af8510000000000001dde000000a6" +
			"000000000000000100000000001daf26000000000000027800000064" +
			"0000000000002bdc00000000002a4c74000000000004dd9c00000080" +
			"000000000000041400000000000277120000000000002c740000006d";

		// 0x203A DispatchClientAddGuildRenown, …_login_guild_invite_accepted. Nine of the ten captured records carry a
		// small amount; this is the one with the largest (0x8b = 139), so it exercises more than the low byte.
		private const string CapturedAddRenown = "00000002000000000000008b0000000000";

		[Fact]
		public void AddGuildRenown_RoundTripsTheCapturedRecord()
		{
			var body = Convert.FromHexString(CapturedAddRenown);
			var parsed = ClientAddGuildRenownCodec.Parse(body);

			Assert.Equal(ClientAddGuildRenown.CapturedRenownType, parsed.RenownType);
			Assert.Equal(139UL, parsed.Amount);
			Assert.False(parsed.GuildOnly);
			Assert.Equal(0, parsed.CapType);
			Assert.Equal(CapturedAddRenown, Convert.ToHexStringLower(ClientAddGuildRenownCodec.Build(parsed)));
		}

		// The amount is a u64, not two u32: a value that needs more than the low word must survive the round trip.
		[Fact]
		public void AddGuildRenown_CarriesTheFullU64Amount()
		{
			var built = ClientAddGuildRenownCodec.Build(ClientAddGuildRenown.CapturedRenownType, 0x0000_00A1_B2C3_D4E5);
			Assert.Equal(0x0000_00A1_B2C3_D4E5UL, ClientAddGuildRenownCodec.Parse(built).Amount);
		}

		// #132's guild-bank pair. Neither body is captured, so what is actually gated here is the WIDTH: the amount and
		// the balance are u64 (the client declares `uint64 amount` and `Guild.Cash` is a u64), and a u32 would silently
		// truncate any guild bank past ~43 gold.
		[Fact]
		public void GuildBank_CarriesU64Amounts()
		{
			const ulong amount = 0x0000_0002_0000_0001; // needs more than 32 bits

			Assert.Equal(sizeof(ulong), GuildWithdrawMoneyCodec.Build(amount).Length);
			Assert.Equal(amount, GuildWithdrawMoneyCodec.Parse(GuildWithdrawMoneyCodec.Build(amount)).AmountTin);

			Assert.Equal(sizeof(ulong), GuildUpdateCashCodec.Build(amount).Length);
			Assert.Equal(amount, GuildUpdateCashCodec.Parse(GuildUpdateCashCodec.Build(amount)).CashTin);
		}

		[Fact]
		public void RenownHistory_EmptyIsTheCapturedLength()
		{
			// The zeroed form this used to send as `new byte[112]` - same bytes, now with a shape behind them.
			var built = GuildRenownHistoryCodec.Build(GuildRenownHistory.Empty);

			Assert.Equal(GuildRenownHistoryCodec.EntryCount * GuildRenownHistoryCodec.EntryLength, built.Length);
			Assert.Equal(112, built.Length);
			Assert.All(built, b => Assert.Equal(0, b));
		}

		[Theory, InlineData(CapturedHistory), InlineData(CapturedHistorySameGuild), InlineData(CapturedHistoryOtherGuild)]
		public void RenownHistory_RoundTripsTheCapturedBody(string captured)
		{
			var parsed = GuildRenownHistoryCodec.Parse(Convert.FromHexString(captured));

			// Every u64 column fits in its low 32 bits across the corpus - the evidence the widths are right, since a
			// mis-split into u32 pairs would put those zeros in the middle of a value instead of above it.
			foreach (var entry in new[] { parsed.Current, parsed.Last, parsed.Highest, parsed.Average })
			{
				Assert.True(entry.Rank <= uint.MaxValue);
				Assert.True(entry.Total <= uint.MaxValue);
				Assert.True(entry.Recent <= uint.MaxValue);
			}

			Assert.Equal(captured, Convert.ToHexStringLower(GuildRenownHistoryCodec.Build(parsed)));
		}

		// What the entries VARY over is what names them, and the corpus splits three ways.
		//
		// Two bodies from the same login pair (…_login and …_login_2) differ in the FIRST entry alone - that is the
		// live "current" row moving while the other three hold.
		[Fact]
		public void RenownHistory_OnlyTheCurrentEntryMovesWithinASession()
		{
			var a = GuildRenownHistoryCodec.Parse(Convert.FromHexString(CapturedHistory));
			var b = GuildRenownHistoryCodec.Parse(Convert.FromHexString(CapturedHistorySameGuild));

			Assert.NotEqual(a.Current, b.Current);
			Assert.Equal(a.Last, b.Last);
			Assert.Equal(a.Highest, b.Highest);
			Assert.Equal(a.Average, b.Average);
		}

		// Across the two GUILDS in the corpus, three of the four entries move - and the third does not, in any capture
		// on either side. An entry that is identical for two different guilds is not that guild's own figure: the
		// third row is realm-wide, which is what "highest" reads as.
		[Fact]
		public void RenownHistory_TheHighestEntryIsIdenticalAcrossGuilds()
		{
			var a = GuildRenownHistoryCodec.Parse(Convert.FromHexString(CapturedHistory));
			var b = GuildRenownHistoryCodec.Parse(Convert.FromHexString(CapturedHistoryOtherGuild));

			Assert.Equal(a.Highest, b.Highest);
			Assert.Equal(new GuildRenownHistoryEntry(11228, 2772084, 318876, 128), a.Highest);

			Assert.NotEqual(a.Current, b.Current);
			Assert.NotEqual(a.Last, b.Last);
			Assert.NotEqual(a.Average, b.Average);
		}
	}
}
