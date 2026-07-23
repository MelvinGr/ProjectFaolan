using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// RegenIIR_c (0x28D0AC99) - retail's per-tick vitals REGEN channel (the highest-frequency V1 type, 1045 captured).
	// DECODED from the client apply method (Ghidra FUN_0079dd10, RegenIIR_c::process): it reads three u32 and ADDS each
	// to one of the target's CURRENT vitals -
	//   field 1 -> HealthCurrent  (stat 0x1B)
	//   field 2 -> ManaCurrent    (stat 0x1FA)
	//   field 3 -> StaminaCurrent (stat 0x1F8)
	// i.e. a compact [+hp][+mana][+stamina] DELTA the client applies itself, in the ×100 wire vitals scale (so a
	// display +9 HP is 900). This is how retail regenerates the three main vitals smoothly without pushing a full
	// 0x96B8DC59 stat block every tick; deltas can be negative (drain). Reader: FUN_0079dea0 stores the three at
	// obj+0x2c/0x30/0x34; apply: FUN_0079dd10 adds them to stats 0x1B/0x1FA/0x1F8.
	public record RegenCodec(uint Handle, byte Env, int HealthDelta, int ManaDelta, int StaminaDelta) : ICodec<RegenCodec>
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.RegenIIR;

		public static RegenCodec Read(FaolanStream s)
		{
			s.ReadUInt32(); // length
			s.ReadUInt32(); // subop
			s.ReadUInt32(); // 0xC350
			var handle = s.ReadUInt32();
			var env = s.ReadByte();
			var hp = (int)s.ReadUInt32();
			var mana = (int)s.ReadUInt32();
			return new RegenCodec(handle, env, hp, mana, (int)s.ReadUInt32());
		}

		public void Write(FaolanStream s)
		{
			s.WriteArrayUInt32Length(body => body.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(Handle).WriteByte(Env)
				.WriteUInt32(HealthDelta).WriteUInt32(ManaDelta).WriteUInt32(StaminaDelta));
		}

		// The DELTAS are the ×100 wire vitals scale (display value × 100), matching the current-vital stats they add to.
		public static byte[] Build(uint handle, int healthDelta, int manaDelta, int staminaDelta, byte env = 0x00)
		{
			return new RegenCodec(handle, env, healthDelta, manaDelta, staminaDelta).ToArray();
		}

		public static RegenCodec Parse(byte[] body)
		{
			return Read(new FaolanStream(body));
		}
	}
}
