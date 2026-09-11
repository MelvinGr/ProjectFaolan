using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     LockedToChangedIIR - a target-lock change, 265 records captured. The client reader (FUN_007a0630) reads two u32,
	///     the locked-to entity reference [class][instance]: Character plus the target handle, or (0, 0) for locked to
	///     nobody. Grammar from that reader; see docs/re/v1-iir-readers.md.
	/// </summary>
	/// <remarks>
	///     The carrier's class must not be asserted to be Character: four captured records carry
	///     <see cref="Constants.EntityClass.Destructible" />, a breakable that is itself locked onto the player, and the
	///     client's reader does not look at the carrier's class at all. StatDamage carries destructible records from the same
	///     capture for the same reason.
	///     <para>The u32 carrier length belongs to the framing sender, not to this body.</para>
	/// </remarks>
	public static class LockedToChangedCodec
	{
		public const uint Opcode = (uint)IirTypes.LockedToChangedIIR;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
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

	/// <summary>
	///     The LockedToChangedIIR body. <c>EntityClass</c> is the carrier's - the entity whose lock changed - and is
	///     Character on 261 of the 265 captured records and Destructible on the other four.
	/// </summary>
	public sealed record LockedToChangedIIR(uint Handle, byte Env, uint TargetClass, uint TargetInstance, uint EntityClass = Constants.EntityClass.Character);
}
