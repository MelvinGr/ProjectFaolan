using Faolan.Core;
using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;

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
	// Both builders emit the WHOLE carrier payload - [u32 len][u32 iirType][record] - as WorldObjectBuilder.Build does,
	// so the bytes here are directly comparable to the captured packet.
	//
	// ⚠️ There are TWO SendWorldIir helpers with OPPOSITE contracts: WorldBroadcaster's private one wraps what you give
	// it in the [u32 len] prefix, while GameServerListener.Outbound's sends the body verbatim. Emitting a prefix-less
	// record through the listener's one puts the IIR TYPE where the client reads a length - it read 0x0DF44B73 as a
	// 234 MB payload and dropped the connection, killing BOTH clients in the team the instant the roster went out.
	public static class GroupMembershipCodec
	{
		public static readonly GameServer0X2000Opcodes[] HandledTypes = [GameServer0X2000Opcodes.AddToGroupIIR, GameServer0X2000Opcodes.RemoveFromGroupIIR];

		// A member JOINED `teamId`, whose leader is `leaderId`.
		public static byte[] BuildAdd(uint memberId, uint teamId, uint leaderId)
		{
			return new FaolanStream().WriteArrayUInt32Length(s => s.WriteUInt32((uint)GameServer0X2000Opcodes.AddToGroupIIR).WriteUInt32(EntityClass.Character).WriteUInt32(memberId)
				.WriteByte(0).WriteUInt32(TeamMessageCodec.TeamClass).WriteUInt32(teamId).WriteUInt32(EntityClass.Character).WriteUInt32(leaderId)).ToArray();
		}

		// A member LEFT `teamId` (left, was kicked, or the team dissolved).
		public static byte[] BuildRemove(uint memberId, uint teamId)
		{
			return new FaolanStream().WriteArrayUInt32Length(s => s.WriteUInt32((uint)GameServer0X2000Opcodes.RemoveFromGroupIIR).WriteUInt32(EntityClass.Character).WriteUInt32(memberId)
				.WriteByte(0).WriteUInt32(TeamMessageCodec.TeamClass).WriteUInt32(teamId)).ToArray();
		}
	}
}
