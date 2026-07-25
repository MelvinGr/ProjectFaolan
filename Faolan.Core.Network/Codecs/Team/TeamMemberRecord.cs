using Faolan.Core;

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
	// 102 bytes for a 10-character name. Only four of the trailing fields carry meaning we can pin from one capture:
	// LEVEL - which the CHAT server's friend reply confirms independently, its trailing byte being the same
	// character's level (0x2C/44 and 0x29/41 for the two captured players) - the map id, and the two per-character values
	// below. The rest are identical across both characters and every message, and are carried as captured; naming
	// them from a single two-character capture would be invention.
	public sealed class TeamMemberRecord : ICodec<TeamMemberRecord>
	{
		// The entity-class word every per-character record carries (EntityClass.Character).
		public const uint CharacterClass = 0x0000C350;

		public uint CharacterId { get; set; }
		public string Name { get; set; } = "";
		public uint Level { get; set; }

		// The playfield the member is in - the client shows it in the team window and greys out members elsewhere.
		public uint MapId { get; set; }

		// Differs per character (2 and 3 for the two captured members) while everything around it is constant, so it
		// is per-character - class or archetype, not pinned by one capture of two characters. [V0]
		public uint Archetype { get; set; }

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
			s.ReadUInt32(); // 0xC350
			var m = new TeamMemberRecord
			{
				CharacterId = s.ReadUInt32(),
				Name = s.ReadString(),
				Level = s.ReadUInt32()
			};
			s.ReadUInt32(); // Field1
			m.MapId = s.ReadUInt32();
			m.Archetype = s.ReadUInt32();
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
			s.WriteUInt32(CharacterClass).WriteUInt32(CharacterId).WriteString(Name).WriteUInt32(Level).WriteUInt32(Field1).WriteUInt32(MapId).WriteUInt32(Archetype).WriteUInt32(Field4)
				.WriteUInt32(Field5).WriteUInt32(Field6).WriteUInt32(Field7).WriteUInt32(Field8);

			// Field9 repeats the map id - the only trailing field that is not constant across the captures, and it
			// tracks MapId in both members of every roster.
			s.WriteUInt32(MapId).WriteUInt32(Field10).WriteUInt32(Field11).WriteUInt32(Field12).WriteUInt32(Field13).WriteUInt32(Field14).WriteUInt32(Field15).WriteUInt32(OpaqueA)
				.WriteUInt32(OpaqueB).WriteByte(OpaqueC).WriteArray(new byte[9]);
		}
	}
}
