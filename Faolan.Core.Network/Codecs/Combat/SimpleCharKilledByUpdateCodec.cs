using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     SimpleCharKilledByUpdateIIR - the explicit "this entity died" trigger, 22 bytes:
	///     <c>[subop][class][dying][u8 env=0][u8 flag=1][class][killerEndpoint]</c>.
	/// </summary>
	/// <remarks>
	///     A health-zero StatUpdate alone does not drive the client's death animation or its dead, release-screen state -
	///     see docs/subsystems/combat.md - and the client only reacts once it also receives this kill record. Decoded from
	///     AssassinL23_..._resurect, where the player-death sequence right before the release screen carries it for the dying
	///     handle. <c>killerEndpoint</c> is the slayer, in the ProtoEndpoint form type &lt;&lt; 16 | instance, and 0 when
	///     unknown or self-inflicted.
	///     <para>The u32 carrier length belongs to the framing sender, not to this body.</para>
	/// </remarks>
	public static class SimpleCharKilledByUpdateCodec
	{
		public const uint Opcode = (uint)IirTypes.SimpleCharKilledByUpdateIIR;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
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

	/// <summary>The SimpleCharKilledByUpdateIIR body. <c>Flag</c> is 1 in every captured record.</summary>
	public sealed record SimpleCharKilledByUpdateIIR(uint DyingHandle, byte Env, byte Flag, uint KillerEndpoint);
}
