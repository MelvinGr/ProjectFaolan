using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// CharacterActionIIR (0x6B6FD368) - one action/ability id against an entity. 41 bytes past the subop:
	//
	//   [u32 subop][u32 class][u32 handle][u32 0][u8 kind][16 x 0x00][u32 actionId][8 x 0x00]
	//
	// ⚠️ `kind` was asserted to be 0x1D, on the grounds that it is constant across every captured record - which was
	// true of the create-editor burst it was decoded from and false of the corpus, where it refused 32 of 537 records
	// (#811). It is a VERB, and the three values split cleanly by direction and content:
	//
	//   0x1D  505 records, all s2c, actionId non-zero  - the GRANT this codec was written for
	//   0x21   31 records, all c2s, everything zero    - the client asking rather than being told
	//   0x1C    1 record,      s2c, actionId 1         - in the untrain-feats capture, so plausibly the inverse
	//
	// The 24 filler bytes are zero on all 537, so the split is the kind byte and nothing else. What 0x21 and 0x1C
	// mean is undecoded [V0]; carrying the byte is what makes the question askable.
	//
	// The create-editor WorldPrep burst streams one per granted id (the same ability/feat id space as the player
	// spawn's ID list - e.g. 0x050E24). Decoded by tools/capture/decode_character_action.py and validated byte-exact
	// against every record in the WorldPrep capture (CreateEditorRecordTests).
	//
	// The u32 carrier length belongs to the SENDER (#530 rule 2).
	public static class CharacterActionCodec
	{
		public const uint Opcode = (uint)IirTypes.CharacterActionIIR;

		// The GRANT verb - what every s2c record carrying an action id uses, and so the default for a Build that does
		// not name one. See the class note: it is carried, not asserted.
		public const byte GrantKind = 0x1D;

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
			var kind = r.ReadU8();
			r.ReadBytes(16);
			var actionId = r.ReadU32Be();
			r.ReadBytes(8);
			return new CharacterActionIIR(handle, actionId, kind);
		}

		internal static void Write(FaolanStream s, CharacterActionIIR v)
		{
			s
				.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(v.Handle)
				.WriteUInt32(0u)
				.WriteByte(v.Kind)
				.WriteArray(new byte[16])
				.WriteUInt32(v.ActionId)
				.WriteArray(new byte[8]);
		}
	}

	// CharacterActionIIR (0x6B6FD368). `Kind` is the record's verb - see the codec's note; it defaults to the grant
	// so every existing caller keeps building the record it always built.
	public sealed record CharacterActionIIR(uint Handle, uint ActionId, byte Kind = CharacterActionCodec.GrantKind);
}
