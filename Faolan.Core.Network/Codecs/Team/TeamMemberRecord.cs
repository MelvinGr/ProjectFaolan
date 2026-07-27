using Faolan.Core.Enums;
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
	// 102 bytes for a 10-character name. The first trailing u32 is the CLASS: the two captured members carry 0x29 and
	// 0x2C, which are exactly Class.Necromancer and Class.Demonologist, and the chat server's friend-add reply ends
	// with the same byte for the same character. (Read as a LEVEL at first, because 0x29/0x2C are also plausible levels
	// and the friend reply "confirmed" it - two independent sightings of the same wrong field. The enum match settles
	// it, and the client agrees: fed a level here, it took the invite, answered it, and refused to raise the invite
	// window, because it cannot build one for a class that does not exist.)
	//
	// Then the map id, and the two per-character values below. The rest are identical across both captured characters
	// and every message, and are carried as captured; naming them from one two-character capture would be invention -
	// including whatever carries the LEVEL, which is not identifiable while both captured members share it.
	public sealed class TeamMemberRecord : ICodec<TeamMemberRecord>
	{
		public uint CharacterId { get; set; }
		public string Name { get; set; } = "";

		// Class.Necromancer / Class.Demonologist in the capture. The client needs a REAL class here - it builds the
		// invite window's class icon from it - so a wrong value silently costs you the window.
		public ConanClass Class { get; set; }

		// The playfield the member is in - the client shows it in the team window and greys out members elsewhere.
		public uint MapId { get; set; }

		// Differs per character (2 and 3 for the two captured members) while everything around it is constant, so it is
		// per-character. Not the class (that is Class above), and not identifiable from one capture of two characters:
		// sex, race and level are all still candidates. [V0]
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

		// The constants, in wire order. Named by position deliberately: every captured record agrees on them, so a
		// name would be a guess. Field1 is the same 5 the friend reply carries. Field7Head is the first THREE bytes of
		// what used to be `Field7 = 0x00000162` - its fourth byte was the PlayfieldProxy version.
		private const uint Field1 = 0x00000005;
		private const uint Field4 = 0x00000000;
		private const uint Field5 = 0x00000006;
		private const uint Field6 = 0x00000000;
		private static readonly byte[] Field7Head = [0x00, 0x00, 0x01];

		// The playfield-instance token both captured members carry. They are two DIFFERENT characters in the same map
		// (4020) carrying the same value, which is what identifies it as an instance token rather than anything
		// per-character. Retained as the default so a from-columns member still builds the captured bytes.
		public const uint CapturedPlayfieldToken = 0x01EDA8B2;

		public static TeamMemberRecord Read(FaolanStream s)
		{
			s.ReadUInt32(); // EntityClass.Character
			var m = new TeamMemberRecord
			{
				CharacterId = s.ReadUInt32(),
				Name = s.ReadString(),
				Class = (ConanClass)s.ReadUInt32()
			};
			s.ReadUInt32(); // Field1
			m.MapId = s.ReadUInt32();
			m.PerCharacterWord = s.ReadUInt32();
			s.ReadUInt32(); // Field4
			s.ReadUInt32(); // Field5
			s.ReadUInt32(); // Field6
			s.ReadArray((uint)Field7Head.Length);
			m.Playfield = PlayfieldProxy.Read(s);
			m.OpaqueA = s.ReadUInt32();
			m.OpaqueB = s.ReadUInt32();
			s.ReadArray(9); // nine zero bytes
			return m;
		}

		public void Write(FaolanStream s)
		{
			s.WriteUInt32(EntityClass.Character).WriteUInt32(CharacterId).WriteString(Name).WriteUInt32((uint)Class).WriteUInt32(Field1).WriteUInt32(MapId).WriteUInt32(PerCharacterWord).WriteUInt32(Field4)
				.WriteUInt32(Field5).WriteUInt32(Field6).WriteArray(Field7Head);

			(Playfield ?? PlayfieldProxy.ForInstance(MapId, CapturedPlayfieldToken)).Write(s);

			s.WriteUInt32(OpaqueA).WriteUInt32(OpaqueB).WriteArray(new byte[9]);
		}
	}
}
