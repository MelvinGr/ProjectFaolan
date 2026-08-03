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
	// It is BOTH a sub-record (every other team message composes it) and, on its own, the whole body of two RPC
	// methods - DispatchYouInvitedToTeam and DispatchYouGotKickedFromTeam send nothing but the member - which is why
	// it declares HandledOpcodes rather than a single Opcode.
	public static class TeamMemberRecordCodec
	{
		// The two methods whose body is a bare member record.
		public static readonly CSPlayerInterfaceOpcodes[] HandledOpcodes = [CSPlayerInterfaceOpcodes.DispatchYouInvitedToTeam, CSPlayerInterfaceOpcodes.DispatchYouGotKickedFromTeam];

		// The constants, in wire order. Named by position deliberately: every captured record agrees on them, so a
		// name would be a guess. Field7Head is the first THREE bytes of what used to be `Field7 = 0x00000162` - its
		// fourth byte was the PlayfieldProxy version.
		private const uint Field4 = 0x00000000;
		private const uint Field5 = 0x00000006;
		private const uint Field6 = 0x00000000;
		private static readonly byte[] Field7Head = [0x00, 0x00, 0x01];

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
			r.ReadBytes(Field7Head.Length);
			m.Playfield = PlayfieldProxy.Read(ref r);
			m.OpaqueA = r.ReadU32Be();
			m.OpaqueB = r.ReadU32Be();
			r.ReadBytes(9); // nine zero bytes

			return m;
		}

		internal static void Write(FaolanStream s, TeamMemberRecord v)
		{
			s.WriteUInt32(EntityClass.Character).WriteUInt32(v.CharacterId).WriteString(v.Name).WriteUInt32((uint)v.Class).WriteUInt32(v.Level).WriteUInt32(v.MapId)
				.WriteUInt32(v.PerCharacterWord).WriteUInt32(Field4).WriteUInt32(Field5).WriteUInt32(Field6).WriteArray(Field7Head);

			(v.Playfield ?? PlayfieldProxy.ForInstance(v.MapId, TeamMemberRecord.CapturedPlayfieldToken)).Write(s);

			s.WriteUInt32(v.OpaqueA).WriteUInt32(v.OpaqueB).WriteArray(new byte[9]);
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

		// An 8-byte per-character opaque followed by nine zero bytes. It appears NOWHERE else in the capture - not in
		// login, not in the snapshot - so it is CS-tier state we cannot derive from anything we send, and there is no
		// basis for computing it. Carried so a captured member round-trips byte-exact; our own members send zero until
		// an in-client check says otherwise.
		//
		// The boundary moved by one byte when the PlayfieldProxy above was framed correctly: what used to be
		// OpaqueA's leading byte is the proxy's trailing bool. So this is now [u32][u32] where it was [u32][u32][u8],
		// and the two captured values are (0x00000301, 0x59EA5C54) and (0x00018181, 0x3BC035EC). Still opaque, but
		// off the right offset. [V0]
		public uint OpaqueA { get; set; }
		public uint OpaqueB { get; set; }

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
