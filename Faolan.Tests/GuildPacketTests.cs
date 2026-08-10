using System;
using System.Security.Cryptography;
using System.Text;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Guild;
using Faolan.Core.Network.Protocol;
using Xunit;

namespace Faolan.Tests
{
	// Golden-byte tests for the guild wire builders against the retail bytes from the hoxxling
	// guild_invite_accepted capture (see tools/capture/decode_guild.py for the decoded grammar).
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

		// The DemoL80 create capture's 0x2044 body, byte-for-byte. Verified against Golden/GuildCreated.bin (338
		// bytes, since deleted) before removal.
		private const string CapturedGuildCreatedSha256 = "3ca011a4a036200d4a9e1547dc0c3e90ea2604227b66e77f987604a054298a5b";

		// #306: the 0x2044 DispatchGuildCreated body for a freshly created guild, byte-exact against the capture
		// (DemoL80 creating "Faolan"): [creator member record][guild header (form byte = request's 0x06)][225-byte
		// create-time tail]. Reproduces the whole 338-byte body from the guild + creator + form.
		[Fact]
		public void GuildCreatedBody_Matches_CapturedBytes()
		{
			var guild = new Guild
			{
				Id = 0x00086C47,
				Name = "Faolan",
				Cash = 0,
				Description = "",
				MessageOfTheDay = ""
			};
			// The creator as the CAPTURE recorded them: this asserts against captured bytes, so the name has to be the
			// one encoded in Golden/GuildCreated.bin. It is one of the three places a real captured character name is
			// still spelled out in the tests (#417, with MailMessageCodecTests and TradepostMailBodyTests) -
			// everywhere else the name is free input and is a pseudonym instead. The `personal-name-ok` marker
			// below is what tells the pre-commit name check that this one is load-bearing rather than an oversight.
			var creator = new Character
			{
				Id = 0x110099BD,
				Name = "DemoL80", // personal-name-ok: the capture's creator name - must match the golden bytes
				GuildRank = GuildRank.Leader,
				Level = 80,
				Class = (ConanClass)0x2C
			};

			var body = GuildCreatedCodec.Build(new GuildCreated(GuildMemberRecords.FromCharacter(creator, true, 0x2B20, 0), GuildHeaders.ForGuildCreate(guild, 0x06)));

			// The retail body's SHA-256 rather than the retail body: the blob is gone from the repo (#555). Equally
			// binding and one-way - and this gate never read the bytes, it only compared against them, which is what
			// makes the substitution lossless here (contrast the goldens that are PARSE inputs; those have to stay).
			Assert.Equal(338, body.Length);
			Assert.Equal(CapturedGuildCreatedSha256, Convert.ToHexStringLower(SHA256.HashData(body)));
		}

		[Fact]
		public void GuildInviteBody_Matches_CapturedBytes()
		{
			var guild = new Guild
			{
				Id = 0x0007585B,
				Name = "Sepulchra Invictus"
			};

			var body = GuildInviteCodec.Build(new GuildInvite(guild.Id, guild.Name, 0x01316E71, "Kyloth"));

			Assert.Equal(CapturedInviteBody, Convert.ToHexStringLower(body));
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
				Class = ConanClass.HeraldOfXotli
			};

			var record = GuildMemberRecords.FromCharacter(hoxxling, true, 0xFAA, 0);

			Assert.Equal(CapturedMemberRecord, Convert.ToHexStringLower(GuildMemberRecordCodec.Build(record)));
			// Rule 1: the record Parse returns is the record Build takes, so the captured bytes rebuild themselves.
			Assert.Equal(CapturedMemberRecord, Convert.ToHexStringLower(GuildMemberRecordCodec.Build(GuildMemberRecordCodec.Parse(Convert.FromHexString(CapturedMemberRecord)))));
		}

		// #463: DispatchGuildName (0x2041) - the reply to ClientRequestGuildName. Capture-verified as a PAIR in
		// xxx_2clients_jumping: frame 1120 asks for guild 0x00075E9A ([0000deb7][00075e9a]) and frame 1131 answers
		// with these bytes. Note the reply carries a BARE guild id, not the ID32 the request used.
		[Fact]
		public void GuildNameBody_Matches_CapturedBytes()
		{
			var body = GuildNameCodec.Build(new GuildName(0x00075E9A, "the chickas"));

			Assert.Equal("00075e9a000b" + Convert.ToHexStringLower(Encoding.Latin1.GetBytes("the chickas")), Convert.ToHexStringLower(body));
			Assert.Equal(new GuildName(0x00075E9A, "the chickas"), GuildNameCodec.Parse(body));
		}

		[Fact]
		public void GuildUpdate_Matches_ExpectedLayout()
		{
			var body = GuildUpdateCodec.Build(0x01316E71, "Sepulchra Invictus");

			// subop 2550C99A + type 0000C350 + char 01316E71 + u16 0 + len 0012 + "Sepulchra Invictus"
			Assert.Equal("2550c99a0000c35001316e7100000012" + Convert.ToHexStringLower(Encoding.Latin1.GetBytes("Sepulchra Invictus")), Convert.ToHexStringLower(body));
		}

		// The in-world guild-tag IIR body (GuildUpdateIIR 0x2550C99A): [subop][0000C350][char][u16 0][string name].
		[Theory, InlineData(0x01316E71u, "Sepulchra Invictus"), InlineData(0x00090000u, "")]
		// empty name clears the tag
		public void GuildUpdate_RoundTrips(uint characterId, string guildName)
		{
			var parsed = GuildUpdateCodec.Parse(GuildUpdateCodec.Build(characterId, guildName));

			Assert.Equal(new GuildUpdateIIR(characterId, guildName), parsed);
		}

		// Rule 4: a mis-routed body throws rather than decoding into something plausible.
		[Fact]
		public void GuildUpdate_Parse_RejectsWrongSubOpcode()
		{
			var body = GuildUpdateCodec.Build(0x01316E71, "X");
			body[0] ^= 0xFF;

			Assert.Throws<CodecParseException>(() => GuildUpdateCodec.Parse(body));
		}
		// ---- the management fan-out (#104) ----------------------------------------------------------------
		//
		// No capture performs a kick, a promotion or a departure, so these are pinned against the CLIENT's readers
		// instead - which is the only evidence there is, and is enough to catch the two field widths that would
		// otherwise be guessed wrong (see below).

		[Fact]
		public void GuildMemberLeftBody_IsAnId32ThenTheNameAsAString()
		{
			var body = GuildMemberLeftCodec.Build(0x01316E71, "Hoxxling");

			// [0000c350][01316e71][0008 "Hoxxling"] - a STRING, unlike the joined twin's whole member element.
			Assert.Equal("0000c35001316e710008" + Convert.ToHexStringLower(Encoding.Latin1.GetBytes("Hoxxling")), Convert.ToHexStringLower(body));
			Assert.Equal(new GuildMemberLeft(0x01316E71, "Hoxxling"), GuildMemberLeftCodec.Parse(body));
		}

		// ⚠️ The ranks here are ONE BYTE each. The client reads both with the raw-byte primitive, not the u32 reader
		// its neighbours use, so a four-byte rank would run the client six bytes past the end of the body.
		[Fact]
		public void GuildRankChangedBody_IsTwoBytes_NewThenOld()
		{
			var body = GuildRankChangedCodec.Build(3, 2);

			Assert.Equal("0302", Convert.ToHexStringLower(body));
			Assert.Equal(new GuildRankChanged(3, 2), GuildRankChangedCodec.Parse(body));
		}

		// ...and in the guild master's own feedback they are u32. The asymmetry is real - two readers, two
		// primitives - which is exactly what a "ranks are one byte everywhere" assumption would break on.
		[Fact]
		public void GuildRankFeedbackBody_IsThreeU32ThenTheName()
		{
			var body = GuildRankFeedbackCodec.Build(GuildGoverningForm.Aquilonian, 2, 3, "Hoxxling");

			Assert.Equal("000000030000000200000003" + "0008" + Convert.ToHexStringLower(Encoding.Latin1.GetBytes("Hoxxling")), Convert.ToHexStringLower(body));
			Assert.Equal(new GuildRankFeedback(GuildGoverningForm.Aquilonian, 2, 3, "Hoxxling"), GuildRankFeedbackCodec.Parse(body));
		}

		[Theory, InlineData(true, "01"), InlineData(false, "00")]
		public void GuildInviteReplyBody_IsTheNameThenABoolByte(bool joined, string flagHex)
		{
			var body = GuildInviteReplyCodec.Build("Hoxxling", joined);

			Assert.Equal("0008" + Convert.ToHexStringLower(Encoding.Latin1.GetBytes("Hoxxling")) + flagHex, Convert.ToHexStringLower(body));
			Assert.Equal(new GuildInviteReply("Hoxxling", joined), GuildInviteReplyCodec.Parse(body));
		}

		[Fact]
		public void GuildInviteNoSuchNameBody_IsJustTheName()
		{
			var body = GuildInviteNoSuchNameCodec.Build("Nosuchguy");

			Assert.Equal("0009" + Convert.ToHexStringLower(Encoding.Latin1.GetBytes("Nosuchguy")), Convert.ToHexStringLower(body));
			Assert.Equal(new GuildInviteNoSuchName("Nosuchguy"), GuildInviteNoSuchNameCodec.Parse(body));
		}

		// The member UPDATE is the member element alone - no leading id, unlike the joined twin. It must therefore
		// round-trip to the same bytes the joined body carries after its ID32.
		[Fact]
		public void GuildMemberUpdateBody_IsTheMemberElementAlone()
		{
			var record = GuildMemberRecordCodec.Parse(Convert.FromHexString(CapturedMemberRecord));
			var body = GuildMemberUpdateCodec.Build(record);

			Assert.Equal(CapturedMemberRecord, Convert.ToHexStringLower(body));
			// Compared as BYTES: GuildMemberRecord carries a byte[] of counters, so record equality is reference
			// equality on it and would pass for two records that agree on nothing else.
			Assert.Equal(CapturedMemberRecord, Convert.ToHexStringLower(GuildMemberUpdateCodec.Build(GuildMemberUpdateCodec.Parse(body))));
		}

		// ⭐ The guild-name refusals (#737). Every one of 0x45-0x48 is a NO-ARG dispatch, so there is no body to
		// build a golden from - the whole message IS the method id, which is what this pins. `annotated_6` records
		// the taken-name case as `0x2047` with an EMPTY body, and `WireOpcode.Of` is what turns the enum member
		// into the number a dump prints.
		//
		// The two middle members are recovered from the client (`CSClientIncoming_c`'s override of the interface
		// slot each dispatches to, and the LDB category-100 token it shows), and the two OUTER ones are the
		// control: they already carried these names, and the same read reproduces both.
		[Theory]
		[InlineData(CSPlayerInterfaceMethods.DispatchGuildNonAlphanumericName, 0x2045)]
		[InlineData(CSPlayerInterfaceMethods.DispatchGuildNameWrongLength, 0x2046)]
		[InlineData(CSPlayerInterfaceMethods.DispatchGuildNameTaken, 0x2047)]
		[InlineData(CSPlayerInterfaceMethods.DispatchGuildBadName, 0x2048)]
		public void TheGuildNameRefusals_RenderAsTheOpcodesTheCaptureRecorded(CSPlayerInterfaceMethods method, int wire)
		{
			Assert.Equal((ushort)wire, WireOpcode.Of(method));
		}
	}
}
