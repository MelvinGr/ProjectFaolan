using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// RegenIIR (0x28D0AC99) - retail's per-tick vitals REGEN channel (the highest-frequency V1 type, 1045 captured).
	// DECODED from the client apply method (Ghidra FUN_0079dd10, RegenIIR_c::process): it reads three u32 and ADDS each
	// to one of the target's CURRENT vitals -
	//   field 1 -> HealthCurrent  (stat 0x1B)
	//   field 2 -> ManaCurrent    (stat 0x1FA)
	//   field 3 -> StaminaCurrent (stat 0x1F8)
	// i.e. a compact [+hp][+mana][+stamina] DELTA the client applies itself, in the x100 wire vitals scale (so a
	// display +9 HP is 900). This is how retail regenerates the three main vitals smoothly without pushing a full
	// 0x96B8DC59 stat block every tick; deltas can be negative (drain). Reader: FUN_0079dea0 stores the three at
	// obj+0x2c/0x30/0x34; apply: FUN_0079dd10 adds them to stats 0x1B/0x1FA/0x1F8.
	//
	// The u32 carrier length belongs to the SENDER (#530 rule 2).
	public static class RegenCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.RegenIIR;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(RegenIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		// The DELTAS are the x100 wire vitals scale (display value x 100), matching the current-vital stats they add to.
		public static byte[] Build(uint handle, int healthDelta, int manaDelta, int staminaDelta, byte env = 0x00)
		{
			return Build(new RegenIIR(handle, env, healthDelta, manaDelta, staminaDelta));
		}

		public static RegenIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(RegenCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static RegenIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "RegenIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			var env = r.ReadU8();
			var health = r.ReadI32Be();
			var mana = r.ReadI32Be();
			return new RegenIIR(handle, env, health, mana, r.ReadI32Be());
		}

		internal static void Write(FaolanStream s, RegenIIR v)
		{
			s
				.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(v.Handle)
				.WriteByte(v.Env)
				.WriteUInt32(v.HealthDelta)
				.WriteUInt32(v.ManaDelta)
				.WriteUInt32(v.StaminaDelta);
		}
	}

	// RegenIIR (0x28D0AC99).
	public sealed record RegenIIR(uint Handle, byte Env, int HealthDelta, int ManaDelta, int StaminaDelta);
}
