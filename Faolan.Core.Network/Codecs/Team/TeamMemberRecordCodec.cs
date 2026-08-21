using System;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Team
{
	// The TEAM MEMBER descriptor: the one repeating record almost every team message carries, and the reason the
	// whole team subsystem is a small amount of code once this exists. Decoded byte-exact from
	// login_2-clients_group-trade-friend (two retail clients on one host, so both sides of every exchange are on
	// tape); TeamTradeFriendCodecTests pins both players' records against that captured hex and gates every message
	// built from it.
	//
	//   [u32 0xC350][u32 characterId][u16 len][name LATIN1] then twenty u32 and a u16.
	//
	// It is BOTH a sub-record (every other team message composes it) and, on its own, the whole body of eleven RPC
	// methods - the three team ones plus every raid message that names a single character - which is why it declares
	// HandledMethods rather than a single Opcode.
	//
	// The client calls it `cCharacter` and its reader is `FUN_005574d0` (#669), which is what turned the three
	// carried-verbatim regions below into named fields: the record's LAST field before the playfield proxy is a
	// BOOL that gates the proxy, and its tail is four u32 and a bool rather than "two words and nine zero bytes".
	// Every captured record still round-trips byte-for-byte - this renames bytes, it does not move any.
	public static class TeamMemberRecordCodec
	{
		// Every method whose whole body is a bare member record. The two team ones are capture-proven; the eight raid
		// ones are the client's own reader - `DispatchRaid*(i_cWho)` / `(i_cCharacter)` all call `FUN_005574d0`,
		// the same reader the team member vector uses (#669). DispatchTeamCharacterApplied - the LFG "X applied to
		// your team" the search's apply button raises - is the same read: its dispatcher `FUN_0133ccf0` calls
		// `FUN_005574d0` and asserts "stream bad after 'i_cWho'" (#889).
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

			// The third bool GATES the proxy - the client reads one only when it is set, so a member with no
			// playfield is a shorter record rather than a record carrying a zeroed proxy.
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

	// 102 bytes for a 10-character name. The first trailing u32 is the CLASS: the two captured members carry 0x29 and
	// 0x2C, which are exactly Class.Necromancer and Class.Demonologist, and the chat server's friend-add reply ends
	// with the same byte for the same character. (Read as a LEVEL at first, because 0x29/0x2C are also plausible levels
	// and the friend reply "confirmed" it - two independent sightings of the same wrong field. The enum match settles
	// it, and the client agrees: fed a level here, it took the invite, answered it, and refused to raise the invite
	// window, because it cannot build one for a class that does not exist.)
	//
	// Then the LEVEL, the map id, and the per-character value below. The rest are identical across both captured
	// characters and every message, and are carried as captured; naming them from one two-character capture would be
	// invention.
	//
	// The level was the one field this record could NOT identify on its own: it sat here as a constant `Field1 = 5`
	// because both captured team members happened to be level 5. DispatchCharacterSearchResult (0x2010) carries the
	// SAME record for several characters at once, and across the five captured bodies that word reads 5, 7, 8 and 10 -
	// every value inside the 5..10 range the client's CharacterSearch asked for, and `Nefalis` appears at 7 in one
	// capture and 8 in a later one. That is a level (#460).
	public sealed record TeamMemberRecord
	{
		// The playfield-instance token both captured members carry. They are two DIFFERENT characters in the same map
		// (4020) carrying the same value, which is what identifies it as an instance token rather than anything
		// per-character. Retained as the default so a from-columns member still builds the captured bytes.
		public const uint CapturedPlayfieldToken = 0x01EDA8B2;

		public uint CharacterId { get; set; }
		public string Name { get; set; } = "";

		// Class.Necromancer / Class.Demonologist in the capture. The client needs a REAL class here - it builds the
		// invite window's class icon from it - so a wrong value silently costs you the window.
		public ConanClass Class { get; set; }

		// The playfield the member is in - the client shows it in the team window and greys out members elsewhere.
		public uint MapId { get; set; }

		// The character's LEVEL - see the class comment. Sent as a constant 5 until the character-search bodies
		// identified it, which meant every team roster, invite and search result described everyone as level 5.
		public uint Level { get; set; }

		// Differs per character (2 and 3 for the two captured members) while everything around it is constant, so it is
		// per-character. Not the class (that is Class above), not the level (that is Level above), and not identifiable
		// from the captures: sex and race are still candidates. [V0]
		public uint PerCharacterWord { get; set; }

		// Two bools the client reads immediately before the playfield gate, zero in every captured record. They are
		// the leading two bytes of what used to be carried as `Field7Head = 00 00 01`; the third byte of that run is
		// the gate itself and is not a field. Named by position because a constant-zero flag names nothing. [V0]
		public bool FlagA { get; set; }
		public bool FlagB { get; set; }

		// The playfield the member's client is looking at, as a PlayfieldProxy - the SAME stream the teleport records
		// carry (client FUN_00a6a600), embedded here rather than being a run of constants (Gitea #454). It reads
		// misaligned if you take the record as all-u32, which is how it was modelled: `Field7 = 0x00000162` had
		// swallowed the proxy's version byte ('b' = 0x62). Read aligned, both captured members decode into all ten
		// proxy fields exactly - class word 0xC79C, this MapId, the ready flag, 0x9C50, and a token.
		//
		// ⚠️ The token is carried VERBATIM (CapturedPlayfieldToken) rather than sourced from the member's live
		// playfield, so our rosters ship the captured session's value. That is deliberate for now: the reading is
		// structural (two codecs agreeing, plus the same value showing up as FriendListCodec's "online stamp") and has
		// not been checked in-client. Gitea #454 / #426. [V0]
		public PlayfieldProxy Playfield { get; set; } = null!;

		// FOUR per-character opaque words and a trailing flag - the client's `FUN_005574d0` reads exactly that after
		// the proxy, which is what split the old "[u32][u32] then nine zero bytes" (#669). C, D and the flag are zero
		// in every captured record; A and B are not, and appear NOWHERE else in the capture - not in login, not in
		// the snapshot - so they are CS-tier state we cannot derive from anything we send, and there is no basis for
		// computing them. Carried so a captured member round-trips byte-exact; our own members send zero until an
		// in-client check says otherwise.
		//
		// The A/B boundary moved by one byte when the PlayfieldProxy above was framed correctly: what used to be
		// OpaqueA's leading byte is the proxy's trailing bool. The two captured values are (0x00000301, 0x59EA5C54)
		// and (0x00018181, 0x3BC035EC). Still opaque, but off the right offset. [V0]
		public uint OpaqueA { get; set; }
		public uint OpaqueB { get; set; }
		public uint OpaqueC { get; set; }
		public uint OpaqueD { get; set; }
		public bool TrailingFlag { get; set; }

		// A member record from a character row. The 9-byte per-character opaque is left at ZERO: it is CS-tier state we
		// cannot derive (see gaps.md), so we send nothing rather than something invented.
		//
		// `mapId` and `playfieldToken` are passed in so the caller can use the member's LIVE playfield: Character.MapId
		// is only written at save points, so it lags a player who has since changed map, and the instance TOKEN is not
		// a column at all - it is minted per playfield instance by the running world (#454). The client shows the
		// playfield in the team window and treats members in another one differently, so both are worth taking from
		// the live world where there is one. A null token keeps the captured constant, which is what a member the game
		// host does not have in a zone has to fall back to.
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
