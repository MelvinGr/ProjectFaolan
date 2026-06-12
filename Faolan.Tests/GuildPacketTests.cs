using System;
using Faolan.Core.Data;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.CSPlayerAgent;
using Xunit;

namespace Faolan.Tests
{
	// Golden-byte tests for the guild wire builders against the retail bytes from the hoxxling
	// guild_invite_accepted capture (see Captures/decode_guild.py for the decoded grammar).
	public class GuildPacketTests
	{
		// The 0x202A invite body Kyloth's invite produced for Hoxxling: guild ID32 (0xDEB7:0x7585B)
		// "Sepulchra Invictus", invitee ID32 (0xC350:0x01316E71), inviter name "Kyloth".
		private const string CapturedInviteBody =
			"0000deb70007585b0012536570756c6368726120496e7669637475730000c35001316e7100064b796c6f7468";

		// Hoxxling's member record from the 0x202F member-joined that followed the accept: rank 0, online,
		// playfield 0xFAA, level 4, class 0x2B (Herald of Xotli), zeroed counters.
		private const string CapturedMemberRecord =
			"030301316e710008486f78786c696e67000100000faa040000002b" + // head..class
			"0000000000000000" + // unknown0 + lastSeen (online)
			"000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"; // 48-byte counters

		[Fact]
		public void GuildInviteBody_Matches_CapturedBytes()
		{
			var guild = new Guild
			{
				Id = 0x0007585B,
				Name = "Sepulchra Invictus"
			};

			var s = new FaolanStream();
			CsPlayerAgentListener.WriteGuildInviteBody(s, guild, 0x01316E71, "Kyloth");

			Assert.Equal(CapturedInviteBody, Convert.ToHexStringLower(s.ToArray()));
		}

		[Fact]
		public void GuildMemberRecord_Matches_CapturedBytes()
		{
			var hoxxling = new Character
			{
				Id = 0x01316E71,
				Name = "Hoxxling",
				GuildRank = 0,
				Level = 4,
				Class = Class.HeraldOfXotli
			};

			var s = new FaolanStream();
			CsPlayerAgentListener.WriteGuildMemberRecord(s, hoxxling, true, 0xFAA, 0);

			Assert.Equal(CapturedMemberRecord, Convert.ToHexStringLower(s.ToArray()));
		}
	}
}
