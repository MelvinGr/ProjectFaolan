using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Text
{
	/// <summary>
	///     s2c <c>FriendAdded</c> - one friend's state:
	///     <c>[u32 characterId][u8 status][u8 level][u32 stamp][u8 class]</c>.
	/// </summary>
	/// <remarks>
	///     The friend list lives on the chat server rather than the game server, which is why this sits beside the other
	///     chat-text codecs. First seen in login_2-clients_group-trade-friend, where the player states the design in chat
	///     ("both friends added, friends are one sided, no accept needed"), then pinned by <c>addfriends</c> and
	///     <c>login-with-friends</c> - six friends of six different levels and classes, one of them offline.
	///     <para>
	///         The client states that layout: the <c>S2C_ADD_BUDDY</c> case of <c>FUN_00a71e00</c> unpacks the message
	///         with the format string <c>"IBBIB"</c> - u32, u8, u8, u32, u8, in exactly this order.
	///     </para>
	///     <para>
	///         Level and class are both here, which is what the client's hover tooltip shows. All six captured trailing
	///         bytes are valid class ids (HeraldOfXotli, BearShaman, Demonologist, TempestOfSet, Demonologist, Barbarian)
	///         while the level byte reads 7/10/5/8/10/5.
	///     </para>
	///     <para>
	///         The stamp is a union on the online flag, and the client splits it itself: the same parser stores it in two
	///         different members of <c>ppj::Buddy_t</c> depending on the flag - offline into <c>[10]</c>, online into
	///         <c>[12]</c>. Offline it is a unix last-seen (the captured offline friend's 0x6A64AD51 is 2026-07-25
	///         12:34:25 UTC, 2h06 before that capture was taken), and <c>Character.LastConnection</c> is what is sent.
	///         Online it is that friend's playfield-instance token: not a time, and each of the five online friends'
	///         stamps appears again inside that same friend's record in the same capture's who-result, ~72 bytes in,
	///         where <c>TeamMemberRecord</c>'s embedded PlayfieldProxy keeps its token. Four carry 0x01EDA8B2 and one
	///         0x01EDAF93, and the who-result reproduces the split friend for friend, so it is not one session constant
	///         seen twice.
	///     </para>
	///     <para>
	///         The online half is not what the friend-list node renders. <c>FriendListNode_c</c>'s buddy-apply
	///         (<c>FUN_006ca170</c>) reads <c>[8]</c> (level), <c>[11]</c> (online) and <c>[10]</c> (the offline
	///         last-seen) and never touches <c>[12]</c>. Its one map lookup keys on <c>[9]</c>, the class byte, against
	///         the <c>RDBPlayerClassData_c</c> class registry, and renders the class name ("unknown" on a miss), so
	///         "unknown" in the friend list is a class-id problem and never a stamp one.
	///     </para>
	///     <para>
	///         The client never reads <c>[12]</c> back at all. <c>ppj::Buddy_t</c> is <c>sizeof == 0x34</c>, held in a
	///         plain vector spanning <c>[chatClient+0x110] .. [+0x114]</c>, so every reachable consumer either iterates
	///         that vector or is handed a record by the buddy-changed signal. Both were swept
	///         (tools/ghidra/DumpBuddyFieldReaders.java, all 139,441 functions): four functions in the chat client
	///         iterate the vector and none touches <c>+0x30</c>, and the co-read sweep for <c>+0x2c</c> - the online
	///         flag, the union's own discriminator - beside <c>+0x30</c> returns only the store
	///         (<c>FUN_00a71e00</c>), the record copy (<c>FUN_00a73b20</c>) and <c>FriendListNode_c</c>, whose
	///         <c>+0x30</c> is a different struct's field. The client therefore parses the online stamp, files it and
	///         drops it, which is why the playfield-token reading stays [V0].
	///     </para>
	///     <para>
	///         <c>online</c> is not a boolean. <c>FriendListNode_c::PresenceCategory</c> (<c>FUN_006ca510</c>) copies the
	///         byte to node <c>+0x30</c> and switches on it: 1 gives category 0, 2 category 1, 3 category 2, and anything
	///         else falls through to the offline branch (category 3). The social-list row builder <c>FUN_016bf320</c>
	///         routes the row into one of five sub-lists by that category and increments its online counter
	///         (<c>+0x438</c>) for 0/1/2 while decrementing it for 3, so all three of 1/2/3 are online states and only
	///         they are. They are <c>SocialStatus.LookingForGroup</c> and <c>SocialStatus.Away</c>: the friends-list icon
	///         table names the categories in exactly this order (offline, online, lfg, away, …), and the client's c2s
	///         <c>SetSocialStatus</c> sends 2 the instant its LFG flag goes on and 1 when it goes off. The field is the
	///         same presence enum in both directions and is typed as one here.
	///     </para>
	///     <para>
	///         Retail sends 1/0 in every captured record, and so does this for a plain login or logout; <c>Online</c>
	///         remains as a bool-shaped overload for the callers that only know whether a connection exists.
	///         <c>GuildService</c>'s member record carries the same online/last-seen union off the same column.
	///     </para>
	/// </remarks>
	public static class FriendStateCodec
	{
		// The captured token, kept as the fallback only: the chat tier sources the friend's real one from the game host
		// through IGameServerDispatcher, and this is what an online friend the game host has no live zone for gets instead.
		// Sending a stale token is still better than sending a last-seen time on the online branch, which is what the client
		// would then read as one.
		public const uint OnlineStamp = 0x01EDA8B2;
		public const ushort Opcode = (ushort)ChatServerInterfaceOpcodes.FriendAdded;

		public static byte[] Build(FriendState value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteUInt32(value.CharacterId).WriteByte((byte)value.Status).WriteByte(value.Level).WriteUInt32(value.Stamp).WriteByte(value.Class);

			return s.ToArray();
		}

		public static byte[] Build(uint characterId, SocialStatus status, byte level, ConanClass classId, uint stamp)
		{
			return Build(new FriendState(characterId, status, level, classId, stamp));
		}

		// For the callers that only know whether a connection exists - a login, a logout, the virtual Claude entry.
		// Online/Offline is what every captured record carries.
		public static byte[] Build(uint characterId, bool online, byte level, ConanClass classId, uint stamp)
		{
			return Build(characterId, online ? SocialStatus.Online : SocialStatus.Offline, level, classId, stamp);
		}

		public static FriendState Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(FriendStateCodec));

			// Locals because the wire order (stamp before class) is not the record's declaration order.
			var characterId = r.ReadU32Be();
			var status = (SocialStatus)r.ReadU8();
			var level = r.ReadU8();
			var stamp = r.ReadU32Be();
			var value = new FriendState(characterId, status, level, (ConanClass)r.ReadU8(), stamp);
			r.EnsureAtEnd();

			return value;
		}
	}

	/// <remarks>
	///     <c>Status</c> is the presence enum (<c>SocialStatus</c>) rather than a flag - see the codec's remarks.
	///     <c>Online</c> is the question the stamp union asks (last-seen versus playfield token), and all three of the
	///     online statuses answer it the same way.
	/// </remarks>
	public sealed record FriendState(uint CharacterId, SocialStatus Status, byte Level, ConanClass Class, uint Stamp)
	{
		public bool Online => Status != SocialStatus.Offline;
	}
}
