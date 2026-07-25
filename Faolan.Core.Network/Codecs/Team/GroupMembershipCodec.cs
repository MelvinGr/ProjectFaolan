using Faolan.Core;
using Faolan.Core.Enums;

namespace Faolan.Core.Network.Codecs.Team
{
	// The group-membership records the GAME server pushes onto the world connection, mirroring what the CS tier does
	// to the team roster: AddToGroupIIR when someone joins, RemoveFromGroupIIR when they leave or are kicked. Both
	// were first captured in login_2-clients_group-trade-friend and were name-only entries in Opcodes.cs until then.
	//
	// So a join produces BOTH: the CS tier's roster dispatches (TeamMessageCodec) and one AddToGroup per member on
	// each member's world connection. The capture sends one record per member - the joiner AND the existing members -
	// to every world connection in the team.
	//
	//   AddToGroupIIR      [ProtoID32 member][u8 0][ProtoID32 team][ProtoID32 leader]   29-byte body
	//   RemoveFromGroupIIR [ProtoID32 member][u8 0][ProtoID32 team]                     21-byte body
	//
	// Both bodies start at the iirType: the carrier's [u32 len] prefix is added by the sender (SendWorldIir), which
	// is the convention the other world-IIR builders that go out that way follow.
	public static class GroupMembershipCodec
	{
		public static readonly GameServer0X2000Opcodes[] HandledTypes = [GameServer0X2000Opcodes.AddToGroupIIR, GameServer0X2000Opcodes.RemoveFromGroupIIR];

		// A member JOINED `teamId`, whose leader is `leaderId`.
		public static byte[] BuildAdd(uint memberId, uint teamId, uint leaderId)
		{
			return new FaolanStream().WriteUInt32((uint)GameServer0X2000Opcodes.AddToGroupIIR).WriteUInt32(TeamMemberRecord.CharacterClass).WriteUInt32(memberId).WriteByte(0)
				.WriteUInt32(TeamMessageCodec.TeamClass).WriteUInt32(teamId).WriteUInt32(TeamMemberRecord.CharacterClass).WriteUInt32(leaderId).ToArray();
		}

		// A member LEFT `teamId` (left, was kicked, or the team dissolved).
		public static byte[] BuildRemove(uint memberId, uint teamId)
		{
			return new FaolanStream().WriteUInt32((uint)GameServer0X2000Opcodes.RemoveFromGroupIIR).WriteUInt32(TeamMemberRecord.CharacterClass).WriteUInt32(memberId).WriteByte(0)
				.WriteUInt32(TeamMessageCodec.TeamClass).WriteUInt32(teamId).ToArray();
		}
	}
}
