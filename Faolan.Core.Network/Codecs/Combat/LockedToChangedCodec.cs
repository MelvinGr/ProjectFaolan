using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// LockedToChangedIIR (0x6B0AEC58) - a target-lock change (265 captured). ReadSubClass (FUN_007a0630) reads two u32 =
	// the locked-to entity ref [class][instance] (0xC350 = Character + the target handle, or (0, 0) for "locked to
	// nobody"). Grammar from the client reader (docs/re/v1-iir-readers.md), byte-exact.
	//
	// ⚠️ The CARRIER's class was asserted to be Character until #811, which refused the 4 records carrying 0xC879
	// (`EntityClass.Destructible`) - a breakable that is itself locked onto the player. The reader does not look at
	// the carrier's class at all, and the codec beside it (StatDamage) turned out to have the same 13 destructible
	// records from the same capture, so this is one phenomenon seen through two codecs, not two bugs.
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
			var entityClass = r.ReadU32Be();
			var handle = r.ReadU32Be();
			var env = r.ReadU8();
			var targetClass = r.ReadU32Be();
			return new LockedToChangedIIR(handle, env, targetClass, r.ReadU32Be(), entityClass);
		}

		internal static void Write(FaolanStream s, LockedToChangedIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(v.EntityClass).WriteUInt32(v.Handle).WriteByte(v.Env).WriteUInt32(v.TargetClass).WriteUInt32(v.TargetInstance);
		}
	}

	// LockedToChangedIIR (0x6B0AEC58). `EntityClass` is the CARRIER's - the entity whose lock changed - and is
	// Character on 261 of the 265 captured records and Destructible on the other 4.
	public sealed record LockedToChangedIIR(uint Handle, byte Env, uint TargetClass, uint TargetInstance, uint EntityClass = Constants.EntityClass.Character);
}
