using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.Team;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Protobuf;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact gates for the team/group, player-trade and friend-list messages against
	// login_2-clients_group-trade-friend - two retail clients on one host, so every exchange is captured from both
	// sides in one file. The hex below is retail's, lifted straight from that capture by frame number: these gates
	// prove the same bytes a Python dissector over all 97 packets does, without needing a corpus a plain clone does
	// not have.
	//
	// The two captured players are 0x01317CF8 (a Necromancer) and 0x013184AE (a Demonologist), both in map 4020 -
	// their classes are what the record's leading trailing-field carries, 0x29 and 0x2C. Their names
	// appear below only because they are inside the bytes being asserted - the same exemption the other
	// captured-name goldens carry (a pseudonym would change the string length and shift every length prefix).
	public class TeamTradeFriendCodecTests
	{
		// The member descriptor as retail sent it, from DispatchYouInvitedToTeam (f678) and DispatchTeamInvite (f680).
		private const string DemonologistMemberHex = "0000c350013184ae000a53747269636b656e6e650000002c0000000500000fb400000003000000000000000600000000000001620000c79c00000fb4" +
												   "0000000000000000000000000000000100009c5001eda8b2000000030159ea5c54000000000000000000";

		private const string NecromancerMemberHex = "0000c35001317cf8000a4a6e6275657674666b6a000000290000000500000fb400000002000000000000000600000000000001620000c79c00000fb4" +
												   "0000000000000000000000000000000100009c5001eda8b200000181813bc035ec000000000000000000";

		private static TeamMemberRecord Demonologist()
		{
			return new TeamMemberRecord
			{
				CharacterId = 0x013184AE,
				Name = "Strickenne",
				Class = ConanClass.Demonologist,
				// Both captured members are level 5 - which is exactly why this word read as a constant until the
				// character-search bodies showed it taking 5/7/8/10 across several characters.
				Level = 5,
				MapId = 4020,
				PerCharacterWord = 3,
				// Framed off the PlayfieldProxy boundary: reading these as (3, 0x0159EA5C, 0x54) is one byte late - the
				// leading 0x00 is the proxy's trailing bool.
				OpaqueA = 0x00000301,
				OpaqueB = 0x59EA5C54
			};
		}

		private static TeamMemberRecord Necromancer()
		{
			return new TeamMemberRecord
			{
				CharacterId = 0x01317CF8,
				Name = "Jnbuevtfkj",
				Class = ConanClass.Necromancer,
				Level = 5,
				MapId = 4020,
				PerCharacterWord = 2,
				OpaqueA = 0x00018181,
				OpaqueB = 0x3BC035EC
			};
		}

		[Theory]
		[InlineData(DemonologistMemberHex, "Strickenne")] // name = captured bytes
		[InlineData(NecromancerMemberHex, "Jnbuevtfkj")] // name = captured bytes
		public void MemberRecord_RoundTripsCapturedBytes(string hex, string name)
		{
			// Read the captured record and write it back: the codec must reproduce retail's bytes exactly, which is
			// what proves the 82-byte trailing block is fully accounted for rather than approximately right.
			var member = TeamMemberRecordCodec.Parse(Convert.FromHexString(hex));

			Assert.Equal(name, member.Name);
			Assert.Equal(4020u, member.MapId);
			Assert.Equal(hex, Convert.ToHexString(TeamMemberRecordCodec.Build(member)).ToLowerInvariant());
		}

		[Fact]
		public void MemberRecord_BuildsFromColumns_ByteExact()
		{
			// The DB-source direction: a member built from typed values, not round-tripped, must equal the capture.
			Assert.Equal(DemonologistMemberHex, Convert.ToHexString(TeamMemberRecordCodec.Build(Demonologist())).ToLowerInvariant());
			Assert.Equal(NecromancerMemberHex, Convert.ToHexString(TeamMemberRecordCodec.Build(Necromancer())).ToLowerInvariant());
		}

		// f678 DispatchYouInvitedToTeam / f1159 DispatchYouGotKickedFromTeam - the member alone.
		[Fact]
		public void MemberOnlyMessages_AreByteExact()
		{
			Assert.Equal(DemonologistMemberHex, Convert.ToHexString(TeamMemberRecordCodec.Build(Demonologist())).ToLowerInvariant());
		}

		// f682 RequestTeamInvite / f680 DispatchTeamInvite - member + u16 0.
		[Fact]
		public void Invite_IsByteExact()
		{
			Assert.Equal(NecromancerMemberHex + "0000", Convert.ToHexString(TeamInviteCodec.Build(Necromancer())).ToLowerInvariant());
		}

		// f683 DispatchReplyTeamInvite - member + u16 0 + byte 0.
		[Fact]
		public void ReplyInvite_IsByteExact()
		{
			Assert.Equal(NecromancerMemberHex + "000000", Convert.ToHexString(ReplyTeamInviteCodec.Build(Necromancer())).ToLowerInvariant());
		}

		// f693 DispatchTeamJoin, f917 DispatchTeamLeave and f918 DispatchSetNewTeamLeader share one layout.
		[Fact]
		public void TeamMemberEvent_IsByteExact()
		{
			Assert.Equal("0000dea90051dd9a" + DemonologistMemberHex, Convert.ToHexString(TeamMemberEventCodec.Build(0x0051DD9A, Demonologist())).ToLowerInvariant());
			Assert.Equal("0000dea90051dd9a" + NecromancerMemberHex, Convert.ToHexString(TeamMemberEventCodec.Build(0x0051DD9A, Necromancer())).ToLowerInvariant());
		}

		// f693 DispatchYouJoinedTeam - the whole roster. The member list is count-framed: retail's 0x00000BD3 is
		// 3 * 0x3F1 for two members, so this also gates that we frame the count rather than emit a constant.
		[Fact]
		public void Roster_IsByteExact()
		{
			var expected = "0000dea90051dd9a" + "0000c35001317cf8" + "00000bd3" + NecromancerMemberHex + DemonologistMemberHex + "000000000100000000000000000000000002";

			Assert.Equal(expected, Convert.ToHexString(TeamRosterCodec.Build(0x0051DD9A, 0x01317CF8, [Necromancer(), Demonologist()])).ToLowerInvariant());
		}

		// f694 - a join pushes AddToGroupIIR onto the world connection, one per member. Retail's packet also carries the
		// carrier's [u32 0x0000001D] length prefix ahead of these bytes; that belongs to the sender, so WorldIir.Send
		// writes it and the codec does not. Getting the pairing wrong is expensive in one direction: an unframed record
		// through the verbatim sender makes the client read the IIR type as a 234 MB payload length and drop the
		// connection.
		[Fact]
		public void AddToGroupIir_IsByteExact()
		{
			Assert.Equal("0df44b730000c35001317cf8000000dea90051dd9a0000c35001317cf8",
				Convert.ToHexString(GroupMembershipCodec.Build(new AddToGroupIIR(0x01317CF8, 0x0051DD9A, 0x01317CF8))).ToLowerInvariant());
			Assert.Equal("0df44b730000c350013184ae000000dea90051dd9a0000c35001317cf8",
				Convert.ToHexString(GroupMembershipCodec.Build(new AddToGroupIIR(0x013184AE, 0x0051DD9A, 0x01317CF8))).ToLowerInvariant());
		}

		// f919 - and a leave pushes RemoveFromGroupIIR.
		[Fact]
		public void RemoveFromGroupIir_IsByteExact()
		{
			Assert.Equal("e4ff0c850000c35001317cf8000000dea90051dd9a", Convert.ToHexString(GroupMembershipCodec.Build(new RemoveFromGroupIIR(0x01317CF8, 0x0051DD9A))).ToLowerInvariant());
			Assert.Equal("e4ff0c850000c350013184ae000000dea90051dd9a", Convert.ToHexString(GroupMembershipCodec.Build(new RemoveFromGroupIIR(0x013184AE, 0x0051DD9A))).ToLowerInvariant());
		}

		// f1260 DispatchRequestTrade, f1268 DispatchStartTrade, f1481 DispatchPartnerAcceptedTrade - one shape, and it
		// is the bare ID32 several other methods already share, so EntityIdCodec owns it.
		[Fact]
		public void Trade_CharacterRefMessages_AreByteExact()
		{
			Assert.Equal("0000c350013184ae", Convert.ToHexString(EntityIdCodec.Build(EntityIds.Character(0x013184AE))).ToLowerInvariant());
			Assert.Equal("0000c35001317cf8", Convert.ToHexString(EntityIdCodec.Build(EntityIds.Character(0x01317CF8))).ToLowerInvariant());
		}

		// f1293 DispatchPartnerUpdatedCash - the 5 tin the capture puts on the table.
		[Fact]
		public void Trade_Cash_IsByteExact()
		{
			Assert.Equal("0000000000000005", Convert.ToHexString(TradeCashCodec.Build(new TradeCash(5))).ToLowerInvariant());
		}

		// f1408 AddTradeItem - bag slot 5 of the backpack (container 104), into trade-window slot 1. The last field is
		// the window slot, not a count: the client's own signature is (ID32 inventory, int itemPos, int destPos), and
		// there is no quantity in the message at all.
		[Fact]
		public void Trade_ParsesAddItem()
		{
			var captured = Convert.FromHexString("0000006801317cf80000000500000001");
			var request = AddTradeItemCodec.Parse(captured);

			Assert.Equal(104u, request.Container);
			Assert.Equal(0x01317CF8u, request.Owner);
			Assert.Equal(5u, request.ItemPos);
			Assert.Equal(1u, request.DestPos);
			Assert.Equal(captured, AddTradeItemCodec.Build(request));
		}

		// f1411 DispatchTradePartnerAddedItem - the length-prefixed item snapshot plus the stack count.
		[Fact]
		public void Trade_AddedItem_IsByteExact()
		{
			const string snapshot = "0888d7e395b3ec8aec2012040800100112060817108bf1301204081a10011a530a140d93991f2715011fcc4e1dd20089d625a4013d0d12140d93991f2715011fcc4e1dd20089d6" +
									"25a4013d0d1a140d93991f2715011fcc4e1dd20089d625a4013d0d25e9550d002de9550d003801455a313848";

			var entry = Convert.FromHexString(snapshot).FromProtobuf<ProtoInventoryGameEntry>();

			Assert.Equal("00000073" + snapshot + "00000001", Convert.ToHexString(TradeAddedItemCodec.Build(new TradeAddedItem(entry, 1))).ToLowerInvariant());
		}

		// The friend-list entries retail restores at login, from `login-with-friends` f150 - six friends of six different
		// levels and classes, one of them offline. This is the data that settled the body: every trailing byte is a valid
		// Class id while the level byte reads 7/10/5/8/10/5, so the two cannot be confused.
		// The stamp is passed per case because it is not constant: five of the six friends carry 0x01EDA8B2 but Llewyl
		// carries 0x01EDAF93 (1761 higher) and the offline one 0x6A64AD51 - so it is per-character, and the codec's
		// default is just the value we have nothing better than.
		[Theory]
		[InlineData("01318405010701eda8b22b", 0x01318405u, true, 7, ConanClass.HeraldOfXotli, 0x01EDA8B2u)]
		[InlineData("01318477010a01edaf931d", 0x01318477u, true, 10, ConanClass.BearShaman, 0x01EDAF93u)]
		[InlineData("013184bb010801eda8b21c", 0x013184BBu, true, 8, ConanClass.TempestOfSet, 0x01EDA8B2u)]
		[InlineData("013184bd010a01eda8b22c", 0x013184BDu, true, 10, ConanClass.Demonologist, 0x01EDA8B2u)]
		[InlineData("013184c0010501eda8b212", 0x013184C0u, true, 5, ConanClass.Barbarian, 0x01EDA8B2u)]
		[InlineData("013184ae00056a64ad512c", 0x013184AEu, false, 5, ConanClass.Demonologist, 0x6A64AD51u)] // the offline one
		public void Friend_State_IsByteExact(string hex, uint id, bool online, byte level, ConanClass classId, uint stamp)
		{
			Assert.Equal(hex, Convert.ToHexString(FriendStateCodec.Build(id, online, level, classId, stamp)).ToLowerInvariant());
		}

		// f866 / f949 / f989 / f1038 / f1076 - the id -> name binding, the message whose absence renders nothing.
		[Theory]
		[InlineData("013184bd000000000006586174657061", 0x013184BDu, "Xatepa")]
		[InlineData("013184c000000000000a54686f7277616c646972", 0x013184C0u, "Thorwaldir")]
		[InlineData("013184bb0000000000074e6566616c6973", 0x013184BBu, "Nefalis")]
		[InlineData("013184770000000000064c6c6577796c", 0x01318477u, "Llewyl")]
		[InlineData("0131840500000000000846656e6978617267", 0x01318405u, "Fenixarg")]
		public void Friend_NameBinding_IsByteExact(string hex, uint id, string name)
		{
			Assert.Equal(hex, Convert.ToHexString(ChatSenderInfoCodec.Build(id, name)).ToLowerInvariant());
		}

		// f862 / f944 / f987 / f1032 / f1072 - the presence push. Five different name lengths, which is what exposed the
		// u16 as `nameLen + 3` rather than the constant 0x000D a single 10-character name suggested.
		[Theory]
		[InlineData("000000000000000001e85c140009530006586174657061", "Xatepa")]
		[InlineData("000000000000000001e85c14000d53000a54686f7277616c646972", "Thorwaldir")]
		[InlineData("000000000000000001e85c14000a5300074e6566616c6973", "Nefalis")]
		[InlineData("000000000000000001e85c1400095300064c6c6577796c", "Llewyl")]
		[InlineData("000000000000000001e85c14000b53000846656e6978617267", "Fenixarg")]
		public void Friend_Presence_IsByteExact(string hex, string name)
		{
			Assert.Equal(hex, Convert.ToHexString(FriendPresenceCodec.Build(name)).ToLowerInvariant());
		}

		// f2249 - a removal is echoed back verbatim.
		[Fact]
		public void Friend_Remove_IsByteExact()
		{
			Assert.Equal("013184ae", Convert.ToHexString(FriendRefCodec.Build(0x013184AE)).ToLowerInvariant());
		}
	}
}
