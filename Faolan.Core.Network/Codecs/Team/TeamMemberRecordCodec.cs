using System;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Team
{
	/// <summary>
	///     The team member descriptor: the one repeating record almost every team message carries.
	/// </summary>
	/// <remarks>
	///     Decoded byte-exact from login_2-clients_group-trade-friend (two retail clients on one host, so both sides of
	///     every exchange are on tape); <c>TeamTradeFriendCodecTests</c> pins both players' records against that captured
	///     hex and gates every message built from it.
	///     <code>
	///     [u32 entityClass][u32 characterId][u16 len][name Latin1] then twenty u32 and a u16.
	///     </code>
	///     <para>
	///         It is both a sub-record - every other team message composes it - and, on its own, the whole body of eleven
	///         RPC methods: the three team ones plus every raid message that names a single character. That is why it
	///         declares <c>HandledMethods</c> rather than a single opcode.
	///     </para>
	///     <para>
	///         The client calls it <c>cCharacter</c> and its reader is <c>FUN_005574d0</c>, which is what names the
	///         regions below: the record's last field before the playfield proxy is a bool that gates the proxy, and its
	///         tail is four u32 and a bool.
	///     </para>
	/// </remarks>
	public static class TeamMemberRecordCodec
	{
		// Every method whose whole body is a bare member record. The two team ones are capture-proven; the eight raid ones
		// are the client's own reader - `DispatchRaid*(i_cWho)` / `(i_cCharacter)` all call `FUN_005574d0`, the same reader
		// the team member vector uses. DispatchTeamCharacterApplied - the LFG "X applied to your team" the search's apply
		// button raises - is the same read: its dispatcher `FUN_0133ccf0` calls `FUN_005574d0` and asserts "stream bad after
		// 'i_cWho'".
		public static readonly CSPlayerInterfaceMethods[] HandledMethods =
		[
			CSPlayerInterfaceMethods.DispatchYouInvitedToTeam, CSPlayerInterfaceMethods.DispatchYouGotKickedFromTeam, CSPlayerInterfaceMethods.DispatchTeamCharacterApplied,
			CSPlayerInterfaceMethods.DispatchRaidCharacterUpdate, CSPlayerInterfaceMethods.DispatchRaidNewMasterLooter, CSPlayerInterfaceMethods.DispatchRaidTargetIsBusy,
			CSPlayerInterfaceMethods.DispatchRaidIsAlreadyInRaid, CSPlayerInterfaceMethods.DispatchRaidInvitationTimedOut,
			CSPlayerInterfaceMethods.DispatchRaidTargetDeclinedRaidInvite, CSPlayerInterfaceMethods.DispatchRaidYouAreNoLongerInvited,
			CSPlayerInterfaceMethods.DispatchRaidCantInviteInvalidSubscriptionFlags, CSPlayerInterfaceMethods.DispatchRaidCantCreateInvalidMemberSubscriptionFlags
		];

		// The constants, in wire order. Named by position deliberately: every captured record agrees on them, so a
		// name would be a guess.
		private const uint Field4 = 0x00000000;
		private const uint Field5 = 0x00000006;
		private const uint Field6 = 0x00000000;

		public static byte[] Build(TeamMemberRecord value)
		{
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static TeamMemberRecord Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(TeamMemberRecordCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static TeamMemberRecord Read(ref SpanReader r)
		{
			r.ReadU32Be(); // EntityClass.Character
			var m = new TeamMemberRecord
			{
				CharacterId = r.ReadU32Be(),
				Name = r.ReadStringU16Length(),
				Class = (ConanClass)r.ReadU32Be()
			};
			m.Level = r.ReadU32Be();
			m.MapId = r.ReadU32Be();
			m.PerCharacterWord = r.ReadU32Be();
			r.ReadU32Be(); // Field4
			r.ReadU32Be(); // Field5
			r.ReadU32Be(); // Field6
			m.FlagA = r.ReadU8() != 0;
			m.FlagB = r.ReadU8() != 0;

			// The third bool gates the proxy: the client reads one only when it is set, so a member with no playfield is a
			// shorter record rather than a record carrying a zeroed proxy.
			if (r.ReadU8() != 0)
				m.Playfield = PlayfieldProxy.Read(ref r);

			m.OpaqueA = r.ReadU32Be();
			m.OpaqueB = r.ReadU32Be();
			m.OpaqueC = r.ReadU32Be();
			m.OpaqueD = r.ReadU32Be();
			m.TrailingFlag = r.ReadU8() != 0;

			return m;
		}

		internal static void Write(FaolanStream s, TeamMemberRecord v)
		{
			s.WriteUInt32(EntityClass.Character).WriteUInt32(v.CharacterId).WriteString(v.Name).WriteUInt32((uint)v.Class).WriteUInt32(v.Level).WriteUInt32(v.MapId)
				.WriteUInt32(v.PerCharacterWord).WriteUInt32(Field4).WriteUInt32(Field5).WriteUInt32(Field6).WriteByte(v.FlagA).WriteByte(v.FlagB).WriteByte(true);

			(v.Playfield ?? PlayfieldProxy.ForInstance(v.MapId, TeamMemberRecord.CapturedPlayfieldToken)).Write(s);

			s.WriteUInt32(v.OpaqueA).WriteUInt32(v.OpaqueB).WriteUInt32(v.OpaqueC).WriteUInt32(v.OpaqueD).WriteByte(v.TrailingFlag);
		}
	}

	/// <summary>
	///     One team member as the wire carries it - 102 bytes for a 10-character name.
	/// </summary>
	/// <remarks>
	///     The first trailing u32 is the class: the two captured members carry 0x29 and 0x2C, which are exactly
	///     <c>ConanClass.Necromancer</c> and <c>ConanClass.Demonologist</c>, and the chat server's friend-add reply ends
	///     with the same byte for the same character. The client agrees: fed a level here it takes the invite, answers it,
	///     and then refuses to raise the invite window, because it cannot build one for a class that does not exist.
	///     <para>
	///         Then the level, the map id, and the per-character value below. The rest are identical across both captured
	///         characters and every message and are carried as captured; naming them from one two-character capture would
	///         be invention.
	///     </para>
	///     <para>
	///         The level cannot be identified from the team captures alone, where both members happen to be level 5.
	///         <c>DispatchCharacterSearchResult</c> carries the same record for several characters at once, and across the
	///         five captured bodies that word reads 5, 7, 8 and 10 - every value inside the 5..10 range the client's
	///         CharacterSearch asked for, with <c>Nefalis</c> appearing at 7 in one capture and 8 in a later one.
	///     </para>
	/// </remarks>
	public sealed record TeamMemberRecord
	{
		// The playfield-instance token both captured members carry. They are two different characters in the same map
		// (4020) carrying the same value, which is what identifies it as an instance token rather than anything
		// per-character. Retained as the default so a from-columns member still builds the captured bytes.
		public const uint CapturedPlayfieldToken = 0x01EDA8B2;

		public uint CharacterId { get; set; }
		public string Name { get; set; } = "";

		// ConanClass.Necromancer / ConanClass.Demonologist in the capture. The client needs a real class here - it builds
		// the invite window's class icon from it - so a wrong value silently costs the window.
		public ConanClass Class { get; set; }

		// The playfield the member is in - the client shows it in the team window and greys out members elsewhere.
		public uint MapId { get; set; }

		// The character's level; see the record's remarks for how it was identified.
		public uint Level { get; set; }

		// Differs per character (2 and 3 for the two captured members) while everything around it is constant, so it is
		// per-character. Not the class and not the level, both of which have their own fields, and not identifiable from the
		// captures: sex and race are still candidates. [V0]
		public uint PerCharacterWord { get; set; }

		// Two bools the client reads immediately before the playfield gate, zero in every captured record. Named by
		// position because a constant-zero flag names nothing. [V0]
		public bool FlagA { get; set; }
		public bool FlagB { get; set; }

		// The playfield the member's client is looking at, as a PlayfieldProxy - the same stream the teleport records carry
		// (client FUN_00a6a600) - embedded here rather than being a run of constants. It reads misaligned if the record is
		// taken as all-u32, which swallows the proxy's version byte; read aligned, both captured members decode into all ten
		// proxy fields exactly.
		//
		// The token is carried verbatim (CapturedPlayfieldToken) rather than sourced from the member's live playfield, so
		// rosters ship the captured session's value. The reading is structural - two codecs agreeing, plus the same value
		// showing up as FriendListCodec's "online stamp" - and has not been checked in-client. [V0]
		public PlayfieldProxy Playfield { get; set; } = null!;

		// Four per-character opaque words and a trailing flag, which is exactly what the client's `FUN_005574d0` reads after
		// the proxy. C, D and the flag are zero in every captured record; A and B are not, and appear nowhere else in the
		// capture - not in login, not in the snapshot - so they are CS-tier state that cannot be derived from anything the
		// server sends. They are carried so a captured member round-trips byte-exact; members built here send zero until an
		// in-client check says otherwise. The two captured values are (0x00000301, 0x59EA5C54) and (0x00018181, 0x3BC035EC).
		// [V0]
		public uint OpaqueA { get; set; }
		public uint OpaqueB { get; set; }
		public uint OpaqueC { get; set; }
		public uint OpaqueD { get; set; }
		public bool TrailingFlag { get; set; }

		/// <summary>A member record from a character row.</summary>
		/// <remarks>
		///     The per-character opaque words are left at zero: they are CS-tier state that cannot be derived (see
		///     docs/protocol/gaps.md), so nothing invented is sent.
		/// </remarks>
		/// <param name="mapId">
		///     Taken from the member's live playfield rather than the row, because <c>Character.MapId</c> is only written at
		///     save points and lags a player who has since changed map.
		/// </param>
		/// <param name="playfieldToken">
		///     The instance token, which is not a column at all - it is minted per playfield instance by the running world. A
		///     null token keeps the captured constant, which is what a member the game host does not have in a zone falls back
		///     to.
		/// </param>
		public static TeamMemberRecord FromCharacter(Character character, uint mapId, uint? playfieldToken = null)
		{
			return new TeamMemberRecord
			{
				CharacterId = character.Id,
				Name = character.Name ?? "", // a nameless character cannot happen post-create, but the wire needs a string
				Class = character.Class,
				Level = character.Level,
				MapId = mapId,
				Playfield = PlayfieldProxy.ForInstance(mapId, playfieldToken ?? CapturedPlayfieldToken)
			};
		}
	}
}
