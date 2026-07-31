using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// SimpleCharKilledByUpdateIIR (0xB066AFC2) - the explicit "this entity died" trigger. A health=0 StatUpdate alone
	// does NOT drive the client's death animation / dead (release-screen) state (docs/subsystems/combat.md, #204/#214);
	// the client only reacts when it also gets this kill record. Decoded from AssassinL23_..._resurect: the player-death
	// sequence right before the 0x2024 release screen carries this for the dying handle.
	//
	//   [subop][class][dying][u8 env=0][u8 flag=1][class][killerEndpoint]     (22 B)
	//
	// `killerEndpoint` is the slayer (ProtoEndpoint-style type<<16|instance); 0 when unknown/self.
	//
	// The u32 carrier length belongs to the SENDER (#530 rule 2).
	public static class SimpleCharKilledByUpdateCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.SimpleCharKilledByUpdateIIR;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(SimpleCharKilledByUpdateIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint dyingHandle, uint killerEndpoint)
		{
			return Build(new SimpleCharKilledByUpdateIIR(dyingHandle, 0x00, 0x01, killerEndpoint));
		}

		public static SimpleCharKilledByUpdateIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(SimpleCharKilledByUpdateCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static SimpleCharKilledByUpdateIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "SimpleCharKilledByUpdateIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var dying = r.ReadU32Be();
			var env = r.ReadU8();
			var flag = r.ReadU8();
			r.ExpectU32Be(EntityClass.Character, "killer class");
			return new SimpleCharKilledByUpdateIIR(dying, env, flag, r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, SimpleCharKilledByUpdateIIR v)
		{
			s
				.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(v.DyingHandle)
				.WriteByte(v.Env)
				.WriteByte(v.Flag)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(v.KillerEndpoint);
		}
	}

	// SimpleCharKilledByUpdateIIR (0xB066AFC2). `Flag` is 1 in every captured record.
	public sealed record SimpleCharKilledByUpdateIIR(uint DyingHandle, byte Env, byte Flag, uint KillerEndpoint);
}
