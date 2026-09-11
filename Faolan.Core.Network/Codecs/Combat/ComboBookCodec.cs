using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     ComboBookIIR - grants one combo into the client's combo book. Sent as part of the create-flow world-prep burst
	///     (the starting combos) and on a level-up ding (the combos unlocked at the new level). The level-up combos are
	///     level-keyed and class-independent: Guardian, Conqueror, Assassin and Mage all get the same set at level 2.
	/// </summary>
	/// <remarks>
	///     Carried as an IIR on the world conversation, the receiver being the player's GameInterface. Big-endian grammar,
	///     decoded from the create and combat-levelup captures and golden-tested in ComboBookCodecTests:
	///     <code>
	///     [u32 subop]        the ComboBookIIR sub-opcode
	///     [u32 class]        the Character marker
	///     [u32 instance]     the target entity object, i.e. the receiver endpoint instance
	///     [u16 0x0001]       an add/type flag, constant in every capture
	///     [u32 comboId]      the combo id granted
	///     </code>
	///     The u32 carrier length belongs to the framing sender; it is 18 on every captured record.
	/// </remarks>
	public static class ComboBookCodec
	{
		public const uint Opcode = (uint)IirTypes.ComboBookIIR;
		private const ushort AddFlag = 0x0001;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
		public static byte[] Build(ComboBookIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint instance, uint comboId)
		{
			return Build(new ComboBookIIR(instance, comboId));
		}

		public static ComboBookIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(ComboBookCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static ComboBookIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "ComboBookIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var instance = r.ReadU32Be();
			r.ExpectU16Be(AddFlag, "add flag");
			return new ComboBookIIR(instance, r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, ComboBookIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.Instance).WriteUInt16(AddFlag).WriteUInt32(v.ComboId);
		}
	}

	/// <summary>The ComboBookIIR body.</summary>
	public sealed record ComboBookIIR(uint Instance, uint ComboId);
}
