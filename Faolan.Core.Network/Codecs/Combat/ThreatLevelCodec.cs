using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// ThreatLevelIIR (0x1745EF6C) - a threat/aggro level (u32) plus a bool (FUN_007a8fd0).
	//
	// The u32 carrier length belongs to the SENDER (#530 rule 2).
	public static class ThreatLevelCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.ThreatLevelIIR;

		// CANONICAL - the only method here that writes bytes.
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

	// ThreatLevelIIR (0x1745EF6C).
	public sealed record ThreatLevelIIR(uint Handle, byte Env, uint Threat, byte Flag);
}
