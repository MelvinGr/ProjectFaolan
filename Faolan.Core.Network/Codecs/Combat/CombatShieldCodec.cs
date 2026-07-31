using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// CombatShieldIIR (0xCFFD0519) - the directional-combat guard broadcast: an entity's blue shield-arc distribution
	// (#20 decode 2, FUN_0113e000). Semantics RE'd from the client's Activate (FUN_0113de90, vtable 0x02041910 slot 2):
	// the u32 is stored as the entity's STAT 0xA5 (165), which the shield UI (CombatRoseView_c / ShieldDirection_n)
	// reads to draw the arcs, and it is DECIMAL-PACKED per direction - hundreds = Left, tens = Mid, units = Right
	// (210 = 2 left, 1 mid, 0 right). The client clamps a digit sum above 3 back to 111.
	//
	// CAPTURE-CONFIRMED, not merely client-RE: all 86 captured records are 21 bytes
	// ([u32 len=17][subop][C350][handle][u8 env=0][u32 value]) and carry REAL distributions - 111, 102, 210, 21,
	// 12, 30, 120, 3 - every one of which decodes to three slices summing to exactly 3. (A now-deleted sibling
	// builder claimed "every captured instance carries value 0"; not one does.)
	//
	// The u32 carrier length belongs to the SENDER (#530 rule 2).
	public static class CombatShieldCodec
	{
		// The retail default the client's own overflow clamp resets stat 0xA5 to: one shield per arc. The most
		// common captured value (17 of 86).
		public const uint DefaultDistribution = 111;
		public const uint Opcode = (uint)IirTypes.CombatShieldIIR;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(CombatShieldIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint handle, uint value, byte env = 0x00)
		{
			return Build(new CombatShieldIIR(handle, env, value));
		}

		public static CombatShieldIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(CombatShieldCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static CombatShieldIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "CombatShieldIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			var env = r.ReadU8();
			return new CombatShieldIIR(handle, env, r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, CombatShieldIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.Handle).WriteByte(v.Env).WriteUInt32(v.Value);
		}
	}

	// CombatShieldIIR (0xCFFD0519).
	public sealed record CombatShieldIIR(uint Handle, byte Env, uint Value);

	// The decimal packing of the guard `Value`. Not encoding - it is how one field's digits mean three arcs, which is
	// the client's own convention, so it stays off the codec's surface (#530 rule 1).
	public static class CombatShieldDistribution
	{
		// The digit sum must not exceed 3 or the client clamps the whole thing back to
		// CombatShieldCodec.DefaultDistribution.
		public static uint Pack(uint left, uint mid, uint right)
		{
			return left * 100 + mid * 10 + right;
		}
	}
}
