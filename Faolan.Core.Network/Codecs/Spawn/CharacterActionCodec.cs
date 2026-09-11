using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Spawn
{
	/// <summary>
	///     CharacterActionIIR - one action or ability id against an entity:
	///     <c>[u32 subop][u32 class][u32 handle][u32 0][u8 kind][16 x 0x00][u32 actionId][8 x 0x00]</c>.
	/// </summary>
	/// <remarks>
	///     <c>kind</c> is a verb and must not be asserted: it looks constant in the create-editor burst and takes three
	///     values over the corpus, splitting cleanly by direction and content.
	///     <code>
	///     0x1D  505 records, all s2c, actionId non-zero  - the grant this codec was written for
	///     0x21   31 records, all c2s, everything zero    - the client asking rather than being told
	///     0x1C    1 record,      s2c, actionId 1         - in the untrain-feats capture, so plausibly the inverse
	///     </code>
	///     The 24 filler bytes are zero on all 537 records, so the kind byte is the only thing that splits them. What 0x21
	///     and 0x1C mean is undecoded. [V0]
	///     <para>
	///         The create-editor WorldPrep burst streams one per granted id, in the same ability and feat id space as the
	///         player spawn's id list. Decoded by tools/capture/decode_character_action.py and validated byte-exact against
	///         every record in the WorldPrep capture (CreateEditorRecordTests). The u32 carrier length belongs to the framing
	///         sender.
	///     </para>
	/// </remarks>
	public static class CharacterActionCodec
	{
		public const uint Opcode = (uint)IirTypes.CharacterActionIIR;

		/// <summary>
		///     The grant verb, which every s2c record carrying an action id uses, and therefore the default for a Build that
		///     does not name one. It is carried rather than asserted.
		/// </summary>
		public const byte GrantKind = 0x1D;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
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

	/// <summary>
	///     The CharacterActionIIR body. <c>Kind</c> is the record's verb and defaults to the grant.
	/// </summary>
	public sealed record CharacterActionIIR(uint Handle, uint ActionId, byte Kind = CharacterActionCodec.GrantKind);
}
