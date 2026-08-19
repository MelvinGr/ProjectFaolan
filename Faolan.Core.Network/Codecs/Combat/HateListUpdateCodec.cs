using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// HateListUpdateIIR (0x8A01E5A1) - a hate/aggro entry: a flag byte then three u32s (FUN_007a93a0); in the capture
	// the trio is [u32 targetClass][u32 target handle][count] - the leading word is the standard [class][handle]
	// entity-reference pair's CLASS half (0xC350 = EntityClass.Character on every captured record), not a marker.
	//
	// ⚠️ The carrier handle is the entity whose THREAT LIST this updates - the PLAYER - and `Target` is the creature
	// that now threatens them. Not the other way round: this comment used to say the carrier was "the hater (the NPC)"
	// and the aggro broadcast followed it, which addressed every record to a handle the client keeps no threat list
	// for. Measured over the corpus, 270 of 270 captured records carry the session player as the carrier.
	//
	// The u32 carrier length belongs to the SENDER (#530 rule 2).
	public static class HateListUpdateCodec
	{
		public const uint Opcode = (uint)IirTypes.HateListUpdateIIR;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(HateListUpdateIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint handle, uint target, uint count = 1, byte flag = 0x00, byte env = 0x00)
		{
			return Build(new HateListUpdateIIR(handle, env, flag, EntityClass.Character, target, count));
		}

		public static HateListUpdateIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(HateListUpdateCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static HateListUpdateIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "HateListUpdateIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			var env = r.ReadU8();
			var flag = r.ReadU8();
			return new HateListUpdateIIR(handle, env, flag, TargetClass: r.ReadU32Be(), Target: r.ReadU32Be(), Count: r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, HateListUpdateIIR v)
		{
			s
				.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(v.Handle)
				.WriteByte(v.Env)
				.WriteByte(v.Flag)
				.WriteUInt32(v.TargetClass)
				.WriteUInt32(v.Target)
				.WriteUInt32(v.Count);
		}
	}

	// HateListUpdateIIR (0x8A01E5A1).
	// (TargetClass, Target) is the wire's usual [class][handle] entity reference - TargetClass was long named
	// "Marker" for its constant 0xC350 face.
	public sealed record HateListUpdateIIR(uint Handle, byte Env, byte Flag, uint TargetClass, uint Target, uint Count);
}
