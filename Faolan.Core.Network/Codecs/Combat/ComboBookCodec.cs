using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// ComboBookIIR (0xA8BBEB7F) - grants ONE combo into the client's combo book. Sent as part of the create-flow
	// world-prep burst (the starting combos) AND on a level-up ding (the combos unlocked at the new level - #209). The
	// level-up combos are level-keyed and class-INDEPENDENT (Guardian/Conqueror/Assassin/Mage all get the same set at L2).
	//
	// Carried as an IIR on the WORLD conversation (opcode 0x2000; receiver = the player's GameInterface). Wire grammar
	// (big-endian), decoded from the create + combat-levelup captures (golden-tested in ComboBookCodecTests):
	//   [u32 0xA8BBEB7F]   ComboBookIIR sub-opcode
	//   [u32 0x0000C350]   Character marker (EntityClass.Character)
	//   [u32 instance]     the target entity object (= the receiver endpoint instance / ClientInstance)
	//   [u16 0x0001]       constant (an add/type flag)
	//   [u32 comboId]      the combo id granted
	//
	// The u32 carrier length belongs to the SENDER (#530 rule 2) - it was 0x12 (18) on every captured record.
	public static class ComboBookCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.ComboBookIIR;
		private const ushort AddFlag = 0x0001;

		// CANONICAL - the only method here that writes bytes.
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

	// ComboBookIIR (0xA8BBEB7F).
	public sealed record ComboBookIIR(uint Instance, uint ComboId);
}
