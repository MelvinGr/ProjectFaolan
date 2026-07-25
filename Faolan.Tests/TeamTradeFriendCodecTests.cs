using System;
using Faolan.Core;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.Team;
using Faolan.Core.Network.Codecs.Text;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact gates for the team/group, player-trade and friend-list messages against
	// login_2-clients_group-trade-friend - two retail clients on one host, so every exchange is captured from BOTH
	// sides in one file. The hex below is RETAIL's, lifted straight from that capture by frame number; the grammar is
	// validated independently to each packet's exact end by tools/capture/decode_team_trade_friend.py.
	//
	// The two captured players are 0x01317CF8 (level 41) and 0x013184AE (level 44), both in map 4020. Their NAMES
	// appear below only because they are inside the bytes being asserted - the same exemption the other
	// captured-name goldens carry (a pseudonym would change the string length and shift every length prefix).
	public class TeamTradeFriendCodecTests
	{
		// The member descriptor as retail sent it, from DispatchYouInvitedToTeam (f678) and DispatchTeamInvite (f680).
		private const string LevelFortyFourMemberHex = "0000c350013184ae000a53747269636b656e6e650000002c0000000500000fb400000003000000000000000600000000000001620000c79c00000fb4" +
		                                           "0000000000000000000000000000000100009c5001eda8b2000000030159ea5c54000000000000000000";

		private const string LevelFortyOneMemberHex = "0000c35001317cf8000a4a6e6275657674666b6a000000290000000500000fb400000002000000000000000600000000000001620000c79c00000fb4" +
		                                           "0000000000000000000000000000000100009c5001eda8b200000181813bc035ec000000000000000000";

		private static TeamMemberRecord LevelFortyFour()
		{
			return new TeamMemberRecord
			{
				CharacterId = 0x013184AE,
				Name = "Strickenne",
				Level = 44,
				MapId = 4020,
				Archetype = 3,
				OpaqueA = 3,
				OpaqueB = 0x0159EA5C,
				OpaqueC = 0x54
			};
		}

		private static TeamMemberRecord LevelFortyOne()
		{
			return new TeamMemberRecord
			{
				CharacterId = 0x01317CF8,
				Name = "Jnbuevtfkj",
				Level = 41,
				MapId = 4020,
				Archetype = 2,
				OpaqueA = 0x181,
				OpaqueB = 0x813BC035,
				OpaqueC = 0xEC
			};
		}

		[Theory]
		[InlineData(LevelFortyFourMemberHex, "Strickenne")] // name = captured bytes
		[InlineData(LevelFortyOneMemberHex, "Jnbuevtfkj")] // name = captured bytes
		public void MemberRecord_RoundTripsCapturedBytes(string hex, string name)
		{
			// Read the captured record and write it back: the codec must reproduce retail's bytes exactly, which is
			// what proves the 82-byte trailing block is fully accounted for rather than approximately right.
			using var read = new FaolanStream(Convert.FromHexString(hex));
			var member = TeamMemberRecord.Read(read);

			Assert.Equal(name, member.Name);
			Assert.Equal(4020u, member.MapId);
			Assert.Equal(hex, Convert.ToHexString(TeamMessageCodec.BuildMemberOnly(member)).ToLowerInvariant());
		}

		[Fact]
		public void MemberRecord_BuildsFromColumns_ByteExact()
		{
			// The DB-source direction: a member built from typed values, not round-tripped, must equal the capture.
			Assert.Equal(LevelFortyFourMemberHex, Convert.ToHexString(TeamMessageCodec.BuildMemberOnly(LevelFortyFour())).ToLowerInvariant());
			Assert.Equal(LevelFortyOneMemberHex, Convert.ToHexString(TeamMessageCodec.BuildMemberOnly(LevelFortyOne())).ToLowerInvariant());
		}

		// f678 DispatchYouInvitedToTeam / f1159 DispatchYouGotKickedFromTeam - the member alone.
		[Fact]
		public void MemberOnlyMessages_AreByteExact()
		{
			Assert.Equal(LevelFortyFourMemberHex, Convert.ToHexString(TeamMessageCodec.BuildMemberOnly(LevelFortyFour())).ToLowerInvariant());
		}

		// f682 RequestTeamInvite / f680 DispatchTeamInvite - member + u16 0.
		[Fact]
		public void Invite_IsByteExact()
		{
			Assert.Equal(LevelFortyOneMemberHex + "0000", Convert.ToHexString(TeamMessageCodec.BuildInvite(LevelFortyOne())).ToLowerInvariant());
		}

		// f683 DispatchReplyTeamInvite - member + u16 0 + byte 0.
		[Fact]
		public void ReplyInvite_IsByteExact()
		{
			Assert.Equal(LevelFortyOneMemberHex + "000000", Convert.ToHexString(TeamMessageCodec.BuildReplyInvite(LevelFortyOne())).ToLowerInvariant());
		}

		// f693 DispatchTeamJoin, f917 DispatchTeamLeave and f918 DispatchSetNewTeamLeader share one layout.
		[Fact]
		public void TeamMemberEvent_IsByteExact()
		{
			Assert.Equal("0000dea90051dd9a" + LevelFortyFourMemberHex, Convert.ToHexString(TeamMessageCodec.BuildTeamMemberEvent(0x0051DD9A, LevelFortyFour())).ToLowerInvariant());
			Assert.Equal("0000dea90051dd9a" + LevelFortyOneMemberHex, Convert.ToHexString(TeamMessageCodec.BuildTeamMemberEvent(0x0051DD9A, LevelFortyOne())).ToLowerInvariant());
		}

		// f693 DispatchYouJoinedTeam - the whole roster. The member list is COUNT-FRAMED: retail's 0x00000BD3 is
		// 3 * 0x3F1 for two members, so this also gates that we frame the count rather than emit a constant.
		[Fact]
		public void Roster_IsByteExact()
		{
			var expected = "0000dea90051dd9a" + "0000c35001317cf8" + "00000bd3" + LevelFortyOneMemberHex + LevelFortyFourMemberHex + "000000000100000000000000000000000002";

			Assert.Equal(expected, Convert.ToHexString(TeamMessageCodec.BuildRoster(0x0051DD9A, 0x01317CF8, [LevelFortyOne(), LevelFortyFour()])).ToLowerInvariant());
		}

		// f694 - a join pushes AddToGroupIIR onto the world connection, one per member.
		[Fact]
		public void AddToGroupIir_IsByteExact()
		{
			Assert.Equal("0000001d0df44b730000c35001317cf8000000dea90051dd9a0000c35001317cf8",
				Convert.ToHexString(GroupMembershipCodec.BuildAdd(0x01317CF8, 0x0051DD9A, 0x01317CF8)).ToLowerInvariant());
			Assert.Equal("0000001d0df44b730000c350013184ae000000dea90051dd9a0000c35001317cf8",
				Convert.ToHexString(GroupMembershipCodec.BuildAdd(0x013184AE, 0x0051DD9A, 0x01317CF8)).ToLowerInvariant());
		}

		// f919 - and a leave pushes RemoveFromGroupIIR.
		[Fact]
		public void RemoveFromGroupIir_IsByteExact()
		{
			Assert.Equal("00000015e4ff0c850000c35001317cf8000000dea90051dd9a", Convert.ToHexString(GroupMembershipCodec.BuildRemove(0x01317CF8, 0x0051DD9A)).ToLowerInvariant());
			Assert.Equal("00000015e4ff0c850000c350013184ae000000dea90051dd9a", Convert.ToHexString(GroupMembershipCodec.BuildRemove(0x013184AE, 0x0051DD9A)).ToLowerInvariant());
		}

		// f1260 DispatchRequestTrade, f1268 DispatchStartTrade, f1481 DispatchPartnerAcceptedTrade - one shape.
		[Fact]
		public void Trade_CharacterRefMessages_AreByteExact()
		{
			Assert.Equal("0000c350013184ae", Convert.ToHexString(PlayerTradeCodec.BuildCharacterRef(0x013184AE)).ToLowerInvariant());
			Assert.Equal("0000c35001317cf8", Convert.ToHexString(PlayerTradeCodec.BuildCharacterRef(0x01317CF8)).ToLowerInvariant());
		}

		// f1293 DispatchPartnerUpdatedCash - the 5 tin the capture puts on the table.
		[Fact]
		public void Trade_Cash_IsByteExact()
		{
			Assert.Equal("0000000000000005", Convert.ToHexString(PlayerTradeCodec.BuildCash(5)).ToLowerInvariant());
		}

		// f1408 AddTradeItem - slot 5 of the backpack (container 104), one item.
		[Fact]
		public void Trade_ParsesAddItem()
		{
			using var s = new FaolanStream(Convert.FromHexString("0000006801317cf80000000500000001"));
			var request = PlayerTradeCodec.ParseAddItem(s);

			Assert.Equal(104u, request.Container);
			Assert.Equal(0x01317CF8u, request.Owner);
			Assert.Equal(5u, request.Slot);
			Assert.Equal(1u, request.Count);
		}

		// f1411 DispatchTradePartnerAddedItem - the length-prefixed item snapshot plus the stack count.
		[Fact]
		public void Trade_AddedItem_IsByteExact()
		{
			const string snapshot = "0888d7e395b3ec8aec2012040800100112060817108bf1301204081a10011a530a140d93991f2715011fcc4e1dd20089d625a4013d0d12140d93991f2715011fcc4e1dd20089d6" +
			                        "25a4013d0d1a140d93991f2715011fcc4e1dd20089d625a4013d0d25e9550d002de9550d003801455a313848";

			Assert.Equal("00000073" + snapshot + "00000001", Convert.ToHexString(PlayerTradeCodec.BuildAddedItem(Convert.FromHexString(snapshot), 1)).ToLowerInvariant());
		}

		// f1687 / f1919 - the chat server's add reply, whose trailing byte is the friend's LEVEL. That is what pins
		// the same field in the team member record.
		[Fact]
		public void Friend_AddReply_IsByteExact()
		{
			Assert.Equal("01317cf8010501eda8b229", Convert.ToHexString(FriendListCodec.BuildAddReply(0x01317CF8, true, 41)).ToLowerInvariant());
			Assert.Equal("013184ae010501eda8b22c", Convert.ToHexString(FriendListCodec.BuildAddReply(0x013184AE, true, 44)).ToLowerInvariant());
		}

		// f1685 / f1911 - the presence push, addressed by name.
		[Fact]
		public void Friend_Presence_IsByteExact()
		{
			Assert.Equal("000000000000000001e85c14000d53000a4a6e6275657674666b6a", Convert.ToHexString(FriendListCodec.BuildPresence("Jnbuevtfkj")).ToLowerInvariant());
			Assert.Equal("000000000000000001e85c14000d53000a53747269636b656e6e65", Convert.ToHexString(FriendListCodec.BuildPresence("Strickenne")).ToLowerInvariant());
		}

		// f2249 - a removal is echoed back verbatim.
		[Fact]
		public void Friend_Remove_IsByteExact()
		{
			Assert.Equal("013184ae", Convert.ToHexString(FriendListCodec.BuildCharacterRef(0x013184AE)).ToLowerInvariant());
		}
	}
}
