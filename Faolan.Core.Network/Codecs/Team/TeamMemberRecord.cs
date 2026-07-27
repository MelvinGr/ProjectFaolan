using Faolan.Core;
using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Team
{
	// The TEAM MEMBER descriptor: the one repeating record almost every team message carries, and the reason the
	// whole team subsystem is a small amount of code once this exists. Decoded byte-exact from
	// login_2-clients_group-trade-friend (two retail clients on one host, so both sides of every exchange are on
	// tape) by tools/capture/decode_team_trade_friend.py, which validates all 97 team/trade/friend packets to their
	// exact end.
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
		public Class Class { get; set; }

		// The playfield the member is in - the client shows it in the team window and greys out members elsewhere.
		public uint MapId { get; set; }

		// Differs per character (2 and 3 for the two captured members) while everything around it is constant, so it is
		// per-character. Not the class (that is Class above), and not identifiable from one capture of two characters:
		// sex, race and level are all still candidates. [V0]
		public uint PerCharacterWord { get; set; }

		// A 9-byte per-character opaque, read as [u32][u32][u8] because only that last BYTE differs between the two
		// members (0x54 / 0xEC) and the three after it are zero in both. It appears NOWHERE else in the capture -
		// not in login, not in the snapshot - so it is CS-tier state we cannot derive from anything we send, and
		// there is no basis for computing it. Carried so a captured member round-trips byte-exact; our own members
		// send zero until an in-client check says otherwise. [V0]
		public uint OpaqueA { get; set; }
		public uint OpaqueB { get; set; }
		public byte OpaqueC { get; set; }

		// The constants, in wire order. Named by position deliberately: every captured record agrees on them, so a
		// name would be a guess. Field1 is the same 5 the friend reply carries.
		private const uint Field1 = 0x00000005;
		private const uint Field4 = 0x00000000;
		private const uint Field5 = 0x00000006;
		private const uint Field6 = 0x00000000;
		private const uint Field7 = 0x00000162;
		private const uint Field8 = 0x0000C79C;
		private const uint Field10 = 0x00000000;
		private const uint Field11 = 0x00000000;
		private const uint Field12 = 0x00000000;
		private const uint Field13 = 0x00000001;
		private const uint Field14 = 0x00009C50;
		private const uint Field15 = 0x01EDA8B2;

		public static TeamMemberRecord Read(FaolanStream s)
		{
			s.ReadUInt32(); // EntityClass.Character
			var m = new TeamMemberRecord
			{
				CharacterId = s.ReadUInt32(),
				Name = s.ReadString(),
				Class = (Class)s.ReadUInt32()
			};
			s.ReadUInt32(); // Field1
			m.MapId = s.ReadUInt32();
			m.PerCharacterWord = s.ReadUInt32();
			for (var i = 0; i < 12; i++)
				s.ReadUInt32(); // Field4..Field15

			m.OpaqueA = s.ReadUInt32();
			m.OpaqueB = s.ReadUInt32();
			m.OpaqueC = s.ReadByte();
			s.ReadArray(9); // nine zero bytes
			return m;
		}

		public void Write(FaolanStream s)
		{
			s.WriteUInt32(EntityClass.Character).WriteUInt32(CharacterId).WriteString(Name).WriteUInt32((uint)Class).WriteUInt32(Field1).WriteUInt32(MapId).WriteUInt32(PerCharacterWord).WriteUInt32(Field4)
				.WriteUInt32(Field5).WriteUInt32(Field6).WriteUInt32(Field7).WriteUInt32(Field8);

			// Field9 repeats the map id - the only trailing field that is not constant across the captures, and it
			// tracks MapId in both members of every roster.
			s.WriteUInt32(MapId).WriteUInt32(Field10).WriteUInt32(Field11).WriteUInt32(Field12).WriteUInt32(Field13).WriteUInt32(Field14).WriteUInt32(Field15).WriteUInt32(OpaqueA)
				.WriteUInt32(OpaqueB).WriteByte(OpaqueC).WriteArray(new byte[9]);
		}
	}
}
