using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Text
{
	// s2c FriendAdded (0x0028) - one friend's state. The friend list lives on the CHAT server, not the game server,
	// which is why this sits beside the other chat-text codecs. First seen in login_2-clients_group-trade-friend,
	// where the player states the design in chat: *"both friends added, friends are one sided, no accept needed"*;
	// then pinned by `addfriends` + `login-with-friends`, six friends of six different levels and classes, one of
	// them offline.
	//
	//   [u32 characterId][u8 online][u8 level][u32 stamp][u8 class]
	//
	// LEVEL and CLASS are BOTH here, which is what the client's hover tooltip shows. The level byte was read as a
	// constant 0x05 until six friends of differing levels made it obvious, and that also settled the trailing byte:
	// all six values are valid Class ids (HeraldOfXotli, BearShaman, Demonologist, TempestOfSet, Demonologist,
	// Barbarian) while the level byte reads 7/10/5/8/10/5.
	//
	// The STAMP is a UNION on the online flag:
	//   OFFLINE -> a unix LAST-SEEN. The captured offline friend's 0x6A64AD51 is 2026-07-25 12:34:25 UTC, 2h06 before
	//              that capture was taken, which is what identified it. We send Character.LastConnection.
	//   ONLINE  -> a PLAYFIELD-INSTANCE TOKEN, i.e. which playfield instance the friend is in. Definitely not a
	//              time (0x01EDA8B2 reads as 1971). The identification comes from TeamMemberRecord, which embeds a
	//              PlayfieldProxy carrying this exact value in its instance-token slot (Gitea #454), and the
	//              distribution here agrees: five of the six captured friends share 0x01EDA8B2 and one carries
	//              0x01EDAF93, 1761 higher - five players in one instance and one in another, which is what a
	//              token does and what neither a constant nor a per-friend id would. Gitea #426. [V0 - structural,
	//              not yet checked in-client.]
	//
	// GuildService's member record already carries the same online/last-seen union off the same column.
	public static class FriendStateCodec
	{
		// The captured token, kept as the FALLBACK only: the chat tier sources the friend's real one from the game
		// host through IGameServerDispatcher, and this is what an online friend the game host has no live zone for
		// gets instead. Sending a stale token is still better than sending a last-seen time on the online branch,
		// which is what the client would then read as one.
		public const uint OnlineStamp = 0x01EDA8B2;
		public const ushort Opcode = (ushort)ChatServerInterfaceOpcodes.FriendAdded;

		public static byte[] Build(FriendState value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteUInt32(value.CharacterId).WriteByte(value.Online).WriteByte(value.Level).WriteUInt32(value.Stamp).WriteByte(value.Class);

			return s.ToArray();
		}

		public static byte[] Build(uint characterId, bool online, byte level, ConanClass classId, uint stamp)
		{
			return Build(new FriendState(characterId, online, level, classId, stamp));
		}

		public static FriendState Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(FriendStateCodec));

			// Locals because the wire order (stamp before class) is not the record's declaration order.
			var characterId = r.ReadU32Be();
			var online = r.ReadU8() != 0;
			var level = r.ReadU8();
			var stamp = r.ReadU32Be();
			var value = new FriendState(characterId, online, level, (ConanClass)r.ReadU8(), stamp);
			r.EnsureAtEnd();

			return value;
		}
	}

	public sealed record FriendState(uint CharacterId, bool Online, byte Level, ConanClass Class, uint Stamp);
}
