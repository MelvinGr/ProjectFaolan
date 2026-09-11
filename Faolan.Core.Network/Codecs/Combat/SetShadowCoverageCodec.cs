using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     SetShadowCoverageIIR - how much shadow the character is standing in, as a percentage.
	/// </summary>
	/// <remarks>
	///     It belongs to the hiding/stealth system rather than to lighting: the client's per-frame player update logs it as
	///     <c>ShadowCoverage = &lt;n&gt;</c> on the GameCode/Hiding debug channel (FUN_01100230, reading the player field at
	///     +0x390). Only the client can compute it, since it owns the lighting and the geometry, so the client is the sender
	///     and the server is told - the opposite direction to most IIRs.
	///     <para>
	///         Grammar from the client reader (ReadSubClass, vtable slot 8, FUN_007a8910): one u32 into obj+0x2C, matching
	///         the four captured bodies exactly:
	///         <code>
	///         00000011 d6c1f0cc 0000c350 1101b4f5 01 00000064   c2s, Hakotep
	///         00000011 d6c1f0cc 0000c350 1101b4f5 00 00000064   s2c echo
	///         [u32 len=17][u32 subop][u32 class][u32 handle][u8 env][u32 percent]
	///         </code>
	///     </para>
	///     <para>
	///         The env byte carries the direction - the client sends 1 and the server echoes 0, the convention ChainKillIIR
	///         and CrimeIIR also use - so Build defaults to the server value.
	///     </para>
	///     <para>
	///         Both captures carry 100, from two different characters on two different maps, so the scale is a percentage
	///         and 100 is fully in shadow, or whatever a character out in the open reports; the corpus has no second value to
	///         tell those apart. It is an integer: read as a float, 0x00000064 is a denormal.
	///     </para>
	///     <para>The u32 carrier length belongs to the framing sender, not to this body.</para>
	/// </remarks>
	public static class SetShadowCoverageCodec
	{
		public const uint Opcode = (uint)IirTypes.SetShadowCoverageIIR;

		/// <summary>The env byte the client sends with; the server's echo carries 0.</summary>
		public const byte ClientEnv = 0x01;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
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

	/// <summary>The SetShadowCoverageIIR body.</summary>
	public sealed record SetShadowCoverageIIR(uint Handle, byte Env, uint Percent);
}
