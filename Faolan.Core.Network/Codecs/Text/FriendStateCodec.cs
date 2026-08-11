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
	// ⭐ The CLIENT states that layout, so it is no longer read off the captures alone (#796): the `S2C_ADD_BUDDY`
	// case of `FUN_00a71e00` unpacks the message with the format string **`"IBBIB"`** - u32, u8, u8, u32, u8, in
	// exactly this order.
	//
	// LEVEL and CLASS are BOTH here, which is what the client's hover tooltip shows. The level byte was read as a
	// constant 0x05 until six friends of differing levels made it obvious, and that also settled the trailing byte:
	// all six values are valid Class ids (HeraldOfXotli, BearShaman, Demonologist, TempestOfSet, Demonologist,
	// Barbarian) while the level byte reads 7/10/5/8/10/5.
	//
	// ⭐⭐ The STAMP is a UNION on the online flag, and the CLIENT SPLITS IT ITSELF (#796). The same parser stores
	// it in two DIFFERENT members of `ppj::Buddy_t` depending on the flag - offline into `[10]`, online into `[12]`.
	// Two slots is the client saying "two meanings", which is what turns the union from a reading into a fact:
	//   OFFLINE -> a unix LAST-SEEN. The captured offline friend's 0x6A64AD51 is 2026-07-25 12:34:25 UTC, 2h06 before
	//              that capture was taken, which is what identified it. We send Character.LastConnection.
	//   ONLINE  -> a PLAYFIELD-INSTANCE TOKEN, i.e. which playfield instance the friend is in. Definitely not a
	//              time (0x01EDA8B2 reads as 1971). The identification comes from TeamMemberRecord, which embeds a
	//              PlayfieldProxy carrying this exact value in its instance-token slot (Gitea #454), and the
	//              distribution here agrees: five of the six captured friends share 0x01EDA8B2 and one carries
	//              0x01EDAF93, 1761 higher - five players in one instance and one in another, which is what a
	//              token does and what neither a constant nor a per-friend id would. Gitea #796. [V0 - structural,
	//              not yet checked in-client.]
	//
	// ⚠️ The ONLINE half is NOT what the friend-list node renders. `FriendListNode_c`'s buddy-apply (`FUN_006ca170`)
	// reads `[8]` (level), `[11]` (online) and `[10]` (the OFFLINE last-seen) and never touches `[12]`. It also does
	// one map lookup, and that lookup was once mis-attributed to this stamp: it keys on `[9]`, the CLASS byte, against
	// the `RDBPlayerClassData_c` class registry, and renders the class NAME (`"unknown"` on a miss). So the online
	// token is consumed somewhere else entirely, and `"unknown"` in the friend list is a class-id problem, never a
	// stamp one.
	//
	// ⛔ **And "somewhere else" is NOWHERE - the client never reads `[12]` back** (#796). `ppj::Buddy_t` is
	// `sizeof == 0x34`, held in a plain vector spanning `[chatClient+0x110] .. [+0x114]` (the add path walks it with
	// `ADD ESI,0x34`), so every reachable consumer either iterates that vector or is handed a record by the
	// buddy-changed signal. Both were swept (`tools/ghidra/DumpBuddyFieldReaders.java`, all 139,441 functions):
	// four functions in the chat client iterate the vector and none of them touches `+0x30`, and the co-read sweep
	// for `+0x2c` (the online flag - the union's own discriminator) beside `+0x30` returns only the STORE
	// (`FUN_00a71e00`), the record COPY (`FUN_00a73b20`) and `FriendListNode_c`, whose `+0x30` is a different
	// struct's field. So the client parses the online stamp, files it, and drops it - and no further reading is
	// recoverable from the client, which is why the playfield-token reading stays [V0].
	//
	// ⚠️⚠️ **`online` is NOT a boolean on the client side.** `FriendListNode_c::PresenceCategory` (`FUN_006ca510`)
	// copies the byte to node `+0x30` and switches on it: **1 -> category 0, 2 -> category 1, 3 -> category 2**, and
	// anything else falls through to the offline branch (category 3). The social-list row builder `FUN_016bf320`
	// then routes the row into one of five sub-lists by that category and INCREMENTS its online counter
	// (`+0x438`) for 0/1/2 while DECREMENTING it for 3 - so all three of 1/2/3 are ONLINE states and only they are.
	// Retail sends 1 (online) / 0 (offline) in every captured record and we send the same, so nothing is wrong
	// today; what `2` and `3` mean is simply not in the corpus. Modelled as `bool` because inventing three names
	// for two unobserved values would be worse than the narrowing.
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
