using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     HateListUpdateIIR - a hate/aggro entry: a flag byte then three u32 (client reader FUN_007a93a0), the trio being
	///     [u32 targetClass][u32 target handle][u32 count]. The leading word is the class half of the wire's usual
	///     [class][handle] entity reference, EntityClass.Character on every captured record.
	/// </summary>
	/// <remarks>
	///     The carrier handle is the entity whose threat list this updates - the player - and <c>Target</c> is the creature
	///     that now threatens them, not the other way round: all 270 captured records carry the session player as the
	///     carrier. Addressing it to the NPC instead would send every record to a handle the client keeps no threat list for.
	///     <para>The u32 carrier length belongs to the framing sender, not to this body.</para>
	/// </remarks>
	public static class HateListUpdateCodec
	{
		public const uint Opcode = (uint)IirTypes.HateListUpdateIIR;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
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

	/// <summary>
	///     The HateListUpdateIIR body. (TargetClass, Target) is the wire's usual [class][handle] entity reference.
	/// </summary>
	public sealed record HateListUpdateIIR(uint Handle, byte Env, byte Flag, uint TargetClass, uint Target, uint Count);
}
