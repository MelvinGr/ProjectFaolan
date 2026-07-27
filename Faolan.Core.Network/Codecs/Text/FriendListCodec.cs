using Faolan.Core.Enums;

namespace Faolan.Core.Network.Codecs.Text
{
	// The friend list, which lives on the CHAT server - not the game server. First seen in
	// login_2-clients_group-trade-friend, where the player states the design in chat: *"both friends added, friends are
	// one sided, no accept needed"*; then pinned by `addfriends` + `login-with-friends`, which is what made the body
	// readable - six friends of six different levels and classes, one of them offline. Sweeping the corpus for a
	// game-tier friend opcode finds only RequestNewJoinHasHostileInFriends (a team-invite safety check), so this really
	// is a chat-tier subsystem, which is why the codec sits beside the other chat-text codecs.
	//
	// An ADD is FOUR packets, and the third is the one that matters most:
	//   c2s 0x0028  [u32 characterId]                                           add
	//   s2c 0x0025  [u64 0][u32 stamp][u16 len][u8 0x53][u16 len][name]          presence, BY NAME
	//   s2c 0x0014  [u32 characterId][u32 0][u16 len][name]                      the id -> NAME binding
	//   s2c 0x0028  [u32 characterId][u8 online][u8 level][u32 stamp][u8 class]   the friend's state
	//
	// A REMOVE is c2s 0x0029 answered by s2c 0x0029, the id echoed back verbatim.
	//
	// At LOGIN the stored list arrives as 0x0014 + 0x0028 PAIRS, one per friend, and NO presence push.
	//
	// ⚠️ Omitting the 0x0014 leaves the client with an id and a state but no NAME for it, and it renders nothing at all -
	// no entry, no error. That is why the list stayed empty while the other two messages were byte-perfect.
	public static class FriendListCodec
	{
		// The tag byte inside the presence push's length-prefixed value.
		private const byte PresenceTag = 0x53;

		// The state's stamp is a UNION on the online flag:
		//   OFFLINE -> a unix LAST-SEEN. The captured offline friend's 0x6A64AD51 is 2026-07-25 12:34:25 UTC, 2h06 before
		//              that capture was taken, which is what identified it. We send Character.LastConnection.
		//   ONLINE  -> a PLAYFIELD-INSTANCE TOKEN, i.e. which playfield instance the friend is in. Definitely not a
		//              time (0x01EDA8B2 reads as 1971). The identification comes from TeamMemberRecord, which embeds a
		//              PlayfieldProxy carrying this exact value in its instance-token slot (Gitea #454), and the
		//              distribution here agrees: five of the six captured friends share 0x01EDA8B2 and one carries
		//              0x01EDAF93, 1761 higher - five players in one instance and one in another, which is what a
		//              token does and what neither a constant nor a per-friend id would. PresenceStamp below sits in
		//              the same 0x01Exxxxx range and is very likely the same kind of value. Gitea #426. [V0 -
		//              structural, not yet checked in-client.]
		//
		// GuildService's member record already carries the same online/last-seen union off the same column.
		public const uint OnlineStamp = 0x01EDA8B2;

		public const uint PresenceStamp = 0x01E85C14;

		// s2c 0x0028 - the friend's state. LEVEL and CLASS are BOTH here, which is what the client's hover tooltip
		// shows. The level byte was read as a constant 0x05 until six friends of differing levels made it obvious, and
		// that also settled the trailing byte: all six values are valid Class ids (HeraldOfXotli, BearShaman,
		// Demonologist, TempestOfSet, Demonologist, Barbarian) while the level byte reads 7/10/5/8/10/5.
		public static byte[] BuildState(uint characterId, bool online, byte level, ConanClass classId, uint stamp)
		{
			return new FaolanStream().WriteUInt32(characterId).WriteByte(online).WriteByte(level).WriteUInt32(stamp).WriteByte(classId).ToArray();
		}

		// s2c 0x0014 - the id -> name binding, the same ChatSenderInfo record a channel post is preceded by.
		public static byte[] BuildNameBinding(uint characterId, string name)
		{
			return new FaolanStream().WriteUInt32(characterId).WriteUInt32(0).WriteString(name).ToArray();
		}

		// s2c 0x0029 - the removal, echoed back verbatim.
		public static byte[] BuildCharacterRef(uint characterId)
		{
			return new FaolanStream().WriteUInt32(characterId).ToArray();
		}

		// s2c 0x0025 - the presence push, addressed by NAME rather than id (unlike every other message here)
		public static byte[] BuildPresence(string name)
		{
			return new FaolanStream().WriteUInt64(0).WriteUInt32(PresenceStamp).WriteArrayUInt16Length(b => b.WriteByte(PresenceTag).WriteString(name)).ToArray();
		}

		// c2s 0x0028 / 0x0029 - the character to add or remove.
		public static uint ParseCharacterId(FaolanStream s)
		{
			return s.ReadUInt32();
		}
	}
}
