using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// EnergyIIR (0x40749B8A) - the actor's energy after an action; stamina-only in the captures.
	//
	//   [subop][class][actor][u8 env=0x01][u32 stamina]     (17 B)
	//
	// The env byte is 0x01 here, not the 0x00 most world IIRs carry - capture-derived (strickenbla combat capture; see
	// protocol_stats_and_combat).
	public static class EnergyCodec
	{
		public const uint Opcode = (uint)IirTypes.EnergyIIR;

		// The env byte every captured EnergyIIR carries.
		public const byte StaminaEnv = 0x01;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(EnergyIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint actor, uint stamina)
		{
			return Build(new EnergyIIR(actor, StaminaEnv, stamina));
		}

		public static EnergyIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(EnergyCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static EnergyIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "EnergyIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var actor = r.ReadU32Be();
			var env = r.ReadU8();
			return new EnergyIIR(actor, env, r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, EnergyIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.Actor).WriteByte(v.Env).WriteUInt32(v.Stamina);
		}
	}

	// EnergyIIR (0x40749B8A).
	public sealed record EnergyIIR(uint Actor, byte Env, uint Stamina);
}
