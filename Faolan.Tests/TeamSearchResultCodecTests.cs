using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Team;
using Xunit;

namespace Faolan.Tests
{
	// Gate for the 0x200F DispatchTeamSearchResult body (#889).
	//
	// The corpus can only pin one half of this: all SIX captured replies are the EMPTY result, because no traced
	// session ever had a team flagged as looking for members. So the empty body is a byte-exact retail golden, and the
	// NON-empty grammar is gated the only way it can be - against the client's own reader, whose two components are
	// themselves capture-proven: a row is a `cTeam` (TeamRosterCodec, byte-exact in TeamTradeFriendCodecTests) then a
	// string. That is what RowIsARosterThenItsMessage asserts, by requiring the row to START with the captured
	// roster's exact bytes.
	public class TeamSearchResultCodecTests
	{
		// Every captured DispatchTeamSearchResult, from six different sessions: an empty count-framed list and a clear
		// i_bMoreHits. Hoxxling_mage-heraldofxotli_login f279, addfriends f795 + f822, login-with-friends f164,
		// login_2-clients_group-trade-friend f1728, and the group-finder/pvp capture f7246.
		private const string CapturedEmptyBody = "000003f100";

		// The two-member roster retail sent as DispatchYouJoinedTeam (f693) - the same bytes TeamTradeFriendCodecTests
		// gates, reused here because a search row IS one of these.
		private const string CapturedRosterHex =
			"0000dea90051dd9a0000c35001317cf800000bd3" +
			"0000c35001317cf8000a4a6e6275657674666b6a000000290000000500000fb400000002000000000000000600000000000001620000c79c00000fb4" +
			"0000000000000000000000000000000100009c5001eda8b200000181813bc035ec000000000000000000" +
			"0000c350013184ae000a53747269636b656e6e650000002c0000000500000fb400000003000000000000000600000000000001620000c79c00000fb4" +
			"0000000000000000000000000000000100009c5001eda8b2000000030159ea5c54000000000000000000" +
			"000000000100000000000000000000000002";

		private static TeamRoster CapturedRoster()
		{
			return TeamRosterCodec.Parse(Convert.FromHexString(CapturedRosterHex));
		}

		private static string Hex(byte[] bytes)
		{
			return Convert.ToHexString(bytes).ToLowerInvariant();
		}

		// The retail golden: a search that matched nothing is [ListFrame(0)][moreHits 0], and both halves have to be
		// there. Dropping the trailing byte is the #408 failure - the client reads the empty frame and then runs off
		// the end of the body.
		[Fact]
		public void EmptyResult_IsTheCapturedBody()
		{
			Assert.Equal(CapturedEmptyBody, Hex(TeamSearchResultCodec.Build(TeamSearchResult.Empty)));
		}

		[Fact]
		public void CapturedEmptyBody_Parses()
		{
			var value = TeamSearchResultCodec.Parse(Convert.FromHexString(CapturedEmptyBody));

			Assert.Empty(value.Rows);
			Assert.False(value.MoreHits);
		}

		// The row grammar, in the only form the evidence supports: the client's reader calls the `cTeam` reader and
		// then the string reader, so a one-row body is the captured roster's bytes followed by a u16-length string,
		// with the list frame around it and the flag after it.
		[Fact]
		public void RowIsARosterThenItsMessage()
		{
			var body = TeamSearchResultCodec.Build(new TeamSearchResult([new TeamSearchResultRow(CapturedRoster(), "Need a healer")], false));

			var expected = "000007e2" // ListFrame.ForCount(1) - and NOT a constant: a one-element list frames as 0x7E2
			             + CapturedRosterHex
			             + "000d4e6565642061206865616c6572" // [u16 13]"Need a healer", Latin1
			             + "00"; // i_bMoreHits

			Assert.Equal(expected, Hex(body));
		}

		[Fact]
		public void MoreHits_IsTheTrailingByte()
		{
			Assert.EndsWith("01", Hex(TeamSearchResultCodec.Build(new TeamSearchResult([], true))));
			Assert.True(TeamSearchResultCodec.Parse(TeamSearchResultCodec.Build(new TeamSearchResult([], true))).MoreHits);
		}

		[Fact]
		public void SeveralRows_RoundTrip()
		{
			var built = new TeamSearchResult([
				new TeamSearchResultRow(CapturedRoster(), "Need a healer"),
				new TeamSearchResultRow(CapturedRoster() with { TeamId = 0x0051DD9B }, ""),
				new TeamSearchResultRow(CapturedRoster() with { TeamId = 0x0051DD9C }, "Villas run, 40+")
			], true);

			var round = TeamSearchResultCodec.Parse(TeamSearchResultCodec.Build(built));

			Assert.Equal(3, round.Rows.Count);
			Assert.True(round.MoreHits);
			Assert.Equal(0x0051DD9Bu, round.Rows[1].Team.TeamId);
			Assert.Equal("Villas run, 40+", round.Rows[2].Message);
			Assert.Equal("Jnbuevtfkj", round.Rows[0].Team.Members[0].Name);
			Assert.Equal(ConanClass.Demonologist, round.Rows[0].Team.Members[1].Class);
			Assert.Equal(Hex(TeamSearchResultCodec.Build(built)), Hex(TeamSearchResultCodec.Build(round)));
		}

		// Parse is strict: the trailing flag is part of the body, so a body that stops after the list is not a short
		// read to be tolerated - it is a message we could not account for.
		[Fact]
		public void Parse_ThrowsOnABodyMissingItsMoreHitsFlag()
		{
			Assert.ThrowsAny<Exception>(() => TeamSearchResultCodec.Parse(Convert.FromHexString("000003f1")));
		}

		[Fact]
		public void Parse_ThrowsOnTrailingBytes()
		{
			Assert.ThrowsAny<Exception>(() => TeamSearchResultCodec.Parse(Convert.FromHexString("000003f10000")));
		}
	}
}
