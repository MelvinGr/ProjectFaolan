using System;
using System.Text;
using Faolan.Core;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs;
using Faolan.CSPlayerAgent;
using Xunit;

namespace Faolan.Tests
{
	// Golden-byte tests for the guild wire builders against the retail bytes from the hoxxling
	// guild_invite_accepted capture (see Captures/scripts/decode_guild.py for the decoded grammar).
	public class GuildPacketTests
	{
		// The 0x202A invite body Kyloth's invite produced for Hoxxling: guild ID32 (0xDEB7:0x7585B)
		// "Sepulchra Invictus", invitee ID32 (0xC350:0x01316E71), inviter name "Kyloth".
		private const string CapturedInviteBody = "0000deb70007585b0012536570756c6368726120496e7669637475730000c35001316e7100064b796c6f7468";

		// Hoxxling's member record from the 0x202F member-joined that followed the accept: rank 0, online,
		// playfield 0xFAA, level 4, class 0x2B (Herald of Xotli), zeroed counters.
		private const string CapturedMemberRecord = "030301316e710008486f78786c696e67000100000faa040000002b" + // head..class
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
			GuildService.WriteGuildInviteBody(s, guild, 0x01316E71, "Kyloth");

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
			GuildService.WriteGuildMemberRecord(s, hoxxling, true, 0xFAA, 0);

			Assert.Equal(CapturedMemberRecord, Convert.ToHexStringLower(s.ToArray()));
		}

		[Fact]
		public void GuildTagBody_Matches_ExpectedLayout()
		{
			var s = new FaolanStream();
			new GuildTagBody(0x01316E71, "Sepulchra Invictus").Write(s);

			// subop 2550C99A + type 0000C350 + char 01316E71 + u16 0 + len 0012 + "Sepulchra Invictus"
			Assert.Equal("2550c99a0000c35001316e7100000012" + Convert.ToHexStringLower(Encoding.UTF8.GetBytes("Sepulchra Invictus")), Convert.ToHexStringLower(s.ToArray()));
		}

		// The in-world guild-tag IIR body (GuildUpdateIIR 0x2550C99A): [subop][0000C350][char][u16 0][string name].
		[Theory, InlineData(0x01316E71u, "Sepulchra Invictus"), InlineData(0x00090000u, "")]
		// empty name clears the tag
		public void GuildTagBody_RoundTrips(uint characterId, string guildName)
		{
			var s = new FaolanStream();
			new GuildTagBody(characterId, guildName).Write(s);
			var body = s.ToArray();

			Assert.True(GuildTagBody.TryParse(body, out var parsedId, out var parsedName));
			Assert.Equal(characterId, parsedId);
			Assert.Equal(guildName, parsedName);
		}

		[Fact]
		public void TryParseGuildTagBody_RejectsWrongSubOpcode()
		{
			var s = new FaolanStream();
			new GuildTagBody(0x01316E71, "X").Write(s);
			var body = s.ToArray();
			body[0] ^= 0xFF;
			Assert.False(GuildTagBody.TryParse(body, out _, out _));
		}
	}
}
