using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// LockedToChangedIIR (0x6B0AEC58) - a target-lock change (58 captured). ReadSubClass (FUN_007a0630) reads two u32 =
	// the locked-to entity ref [class][instance] (observed 0xC350 = Character + the target handle). Grammar from the
	// client reader (docs/re/v1-iir-readers.md), byte-exact.
	//
	// The u32 carrier length belongs to the SENDER (#530 rule 2).
	public static class LockedToChangedCodec
	{
		public const uint Opcode = (uint)IirTypes.LockedToChangedIIR;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(LockedToChangedIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint handle, uint targetInstance, uint targetClass = EntityClass.Character, byte env = 0x00)
		{
			return Build(new LockedToChangedIIR(handle, env, targetClass, targetInstance));
		}

		public static LockedToChangedIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(LockedToChangedCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static LockedToChangedIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "LockedToChangedIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			var env = r.ReadU8();
			var targetClass = r.ReadU32Be();
			return new LockedToChangedIIR(handle, env, targetClass, r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, LockedToChangedIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.Handle).WriteByte(v.Env).WriteUInt32(v.TargetClass).WriteUInt32(v.TargetInstance);
		}
	}

	// LockedToChangedIIR (0x6B0AEC58).
	public sealed record LockedToChangedIIR(uint Handle, byte Env, uint TargetClass, uint TargetInstance);
}
