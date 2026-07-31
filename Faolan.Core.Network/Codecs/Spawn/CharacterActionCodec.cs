using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// CharacterActionIIR (0x6B6FD368) - grants one action/ability id to an entity. 41 bytes past the subop:
	//
	//   [u32 subop][u32 class][u32 handle][u32 0][u8 0x1D][16 x 0x00][u32 actionId][8 x 0x00]
	//
	// The create-editor WorldPrep burst streams one per granted id (the same ability/feat id space as the player
	// spawn's ID list - e.g. 0x050E24). Decoded by tools/capture/decode_character_action.py and validated byte-exact
	// against every record in the WorldPrep capture (CreateEditorRecordTests).
	//
	// The u32 carrier length belongs to the SENDER (#530 rule 2).
	public static class CharacterActionCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.CharacterActionIIR;

		// The record's one non-zero literal: a kind byte ahead of the 16 zero bytes. Constant across every captured
		// record, so it is asserted rather than carried.
		private const byte Kind = 0x1D;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(CharacterActionIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static CharacterActionIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(CharacterActionCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static CharacterActionIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "CharacterActionIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			r.ExpectU32Be(0, "leading zero word");
			r.ExpectU8(Kind, "action kind");
			r.ReadBytes(16);
			var actionId = r.ReadU32Be();
			r.ReadBytes(8);
			return new CharacterActionIIR(handle, actionId);
		}

		internal static void Write(FaolanStream s, CharacterActionIIR v)
		{
			s
				.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(v.Handle)
				.WriteUInt32(0u)
				.WriteByte(Kind)
				.WriteArray(new byte[16])
				.WriteUInt32(v.ActionId)
				.WriteArray(new byte[8]);
		}
	}

	// CharacterActionIIR (0x6B6FD368).
	public sealed record CharacterActionIIR(uint Handle, uint ActionId);
}
