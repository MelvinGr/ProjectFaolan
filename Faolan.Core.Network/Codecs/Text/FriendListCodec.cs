using Faolan.Core;

namespace Faolan.Core.Network.Codecs.Text
{
	// The friend list, which lives on the CHAT server - not the game server. Decoded from
	// login_2-clients_group-trade-friend, where the player states the design in chat: *"both friends added, friends
	// are one sided, no accept needed"*. Sweeping that whole capture for a game-tier friend opcode finds only
	// RequestNewJoinHasHostileInFriends (a team-invite safety check), so this really is a chat-tier subsystem, which
	// is why the codec sits beside the other chat-text codecs.
	//
	//   c2s 0x0028  [u32 characterId]                                       add
	//   s2c 0x0028  [u32 characterId][u8 online][u8 5][u32 stamp][u8 level]  the added friend's state
	//   c2s 0x0029  [u32 characterId]                                       remove
	//   s2c 0x0029  [u32 characterId]                                       echoed back
	//   s2c 0x0025  [u64 0][u32 stamp][u16][u8][u16 len][name]              presence push, BY NAME
	//
	// One-sided with no accept handshake, so there is no invite state machine: an add is a write plus a presence
	// subscription. The add reply is also what pins the team member record's LEVEL field - its trailing byte is the
	// same character's level (0x2C/44, 0x29/41 for the two captured players).
	public static class FriendListCodec
	{
		// Carried in the add reply and the presence push. 0x01EDA8B2 is the same value the team member record has at
		// field 15, so it is server-wide rather than per-character; 0x01E85C14 is the presence push's. Neither is
		// derivable from anything we send. [V0]
		public const uint AddReplyStamp = 0x01EDA8B2;

		public const uint PresenceStamp = 0x01E85C14;

		// s2c 0x0028 - the state of the friend just added. `online` is 1 in every captured reply (you can only add a
		// character the chat server can resolve); `field` is the same 5 the team member record carries.
		public static byte[] BuildAddReply(uint characterId, bool online, byte level, byte field = 5, uint stamp = AddReplyStamp)
		{
			return new FaolanStream().WriteUInt32(characterId).WriteByte(online).WriteByte(field).WriteUInt32(stamp).WriteByte(level).ToArray();
		}

		// s2c 0x0029 - the removal, echoed back verbatim.
		public static byte[] BuildCharacterRef(uint characterId)
		{
			return new FaolanStream().WriteUInt32(characterId).ToArray();
		}

		// s2c 0x0025 - the presence push, addressed by NAME rather than id (unlike every other message here).
		public static byte[] BuildPresence(string name, ushort a = 0x000D, byte b = 0x53, uint stamp = PresenceStamp)
		{
			return new FaolanStream().WriteUInt64(0).WriteUInt32(stamp).WriteUInt16(a).WriteByte(b).WriteString(name).ToArray();
		}

		// c2s 0x0028 / 0x0029 - the character to add or remove.
		public static uint ParseCharacterId(FaolanStream s)
		{
			return s.ReadUInt32();
		}
	}
}
