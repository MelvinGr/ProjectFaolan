using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     ThreatLevelIIR - a threat/aggro level (u32) plus a bool; client reader FUN_007a8fd0.
	/// </summary>
	/// <remarks>The u32 carrier length belongs to the framing sender, not to this body.</remarks>
	public static class ThreatLevelCodec
	{
		public const uint Opcode = (uint)IirTypes.ThreatLevelIIR;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
		public static byte[] Build(ThreatLevelIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint handle, uint threat, byte flag, byte env = 0x00)
		{
			return Build(new ThreatLevelIIR(handle, env, threat, flag));
		}

		public static ThreatLevelIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(ThreatLevelCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static ThreatLevelIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "ThreatLevelIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			var env = r.ReadU8();
			var threat = r.ReadU32Be();
			return new ThreatLevelIIR(handle, env, threat, r.ReadU8());
		}

		internal static void Write(FaolanStream s, ThreatLevelIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.Handle).WriteByte(v.Env).WriteUInt32(v.Threat).WriteByte(v.Flag);
		}
	}

	/// <summary>The ThreatLevelIIR body.</summary>
	public sealed record ThreatLevelIIR(uint Handle, byte Env, uint Threat, byte Flag);
}
