using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// SetShadowCoverageIIR (0xD6C1F0CC) - how much SHADOW the character is standing in, as a percentage.
	//
	// It belongs to the HIDING/stealth system, not to lighting: the client's per-frame player update logs it as
	// `ShadowCoverage = <n>` on the `GameCode/Hiding` debug channel (Ghidra FUN_01100230, reading the player field at
	// +0x390). Only the client can compute it - it owns the lighting and the geometry - so the CLIENT is the sender
	// and the server is told, which is the opposite direction to most IIRs.
	//
	// Grammar from the client reader (ReadSubClass = vtable slot 8, FUN_007a8910): one u32 into obj+0x2C. That matches
	// the four captured bodies exactly:
	//
	//   00000011 d6c1f0cc 0000c350 1101b4f5 01 00000064   c2s, Hakotep
	//   00000011 d6c1f0cc 0000c350 1101b4f5 00 00000064   s2c echo
	//   [u32 len=17][u32 subop][u32 class][u32 handle][u8 env][u32 percent]
	//
	// ⚠️ The ENV byte carries the direction: the client sends 0x01 and the server echoes 0x00 - the same convention
	// ChainKillIIR and CrimeIIR use. Build() defaults to the SERVER value.
	//
	// Both captures carry 100, from two different characters on two different maps, so the scale is a percentage and
	// 100 is "fully in shadow" (or the value a character out in the open reports - the corpus has no second value to
	// tell those apart). It is an INTEGER: 0x00000064 read as a float is a denormal.
	//
	// The u32 carrier length belongs to the SENDER (#530 rule 2).
	public static class SetShadowCoverageCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.SetShadowCoverageIIR;

		/// <summary>The env byte the CLIENT sends with; the server's echo carries 0.</summary>
		public const byte ClientEnv = 0x01;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(SetShadowCoverageIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint handle, uint percent, byte env = 0x00)
		{
			return Build(new SetShadowCoverageIIR(handle, env, percent));
		}

		public static SetShadowCoverageIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(SetShadowCoverageCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static SetShadowCoverageIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "SetShadowCoverageIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			var env = r.ReadU8();
			return new SetShadowCoverageIIR(handle, env, r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, SetShadowCoverageIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.Handle).WriteByte(v.Env).WriteUInt32(v.Percent);
		}
	}

	// SetShadowCoverageIIR (0xD6C1F0CC).
	public sealed record SetShadowCoverageIIR(uint Handle, byte Env, uint Percent);
}
