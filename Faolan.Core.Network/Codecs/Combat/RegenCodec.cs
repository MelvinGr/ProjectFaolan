using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     RegenIIR - retail's per-tick vitals regen channel, and the highest-frequency V1 type (1045 captured). It is a
	///     compact [+hp][+mana][+stamina] delta the client applies itself, in the x100 wire vitals scale, so a display +9 HP
	///     is 900. Deltas may be negative (drain).
	/// </summary>
	/// <remarks>
	///     Decoded from the client's apply method (FUN_0079dd10, RegenIIR_c::process): it reads three u32 and adds each to
	///     one of the target's current vitals - field 1 to HealthCurrent (stat 0x1B), field 2 to ManaCurrent (stat 0x1FA),
	///     field 3 to StaminaCurrent (stat 0x1F8). The reader FUN_0079dea0 stores the three at obj+0x2c/0x30/0x34. This is
	///     how retail regenerates the three main vitals smoothly without pushing a full stat block every tick.
	///     <para>The u32 carrier length belongs to the framing sender, not to this body.</para>
	/// </remarks>
	public static class RegenCodec
	{
		public const uint Opcode = (uint)IirTypes.RegenIIR;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
		public static byte[] Build(RegenIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		/// <summary>
		///     The deltas are in the x100 wire vitals scale, matching the current-vital stats they are added to.
		/// </summary>
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

	/// <summary>The RegenIIR body.</summary>
	public sealed record RegenIIR(uint Handle, byte Env, int HealthDelta, int ManaDelta, int StaminaDelta);
}
