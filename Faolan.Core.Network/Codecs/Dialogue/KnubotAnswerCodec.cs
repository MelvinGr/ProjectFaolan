using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Dialogue
{
	// KnubotAnswerIIR_c (0x1E9E85E4) - the c2s answer pick. Split out of the KnubotCodec family (#530 rule 5)
	// because it does NOT carry the KnubotBase header the four s2c records share: it is a different grammar that
	// merely belongs to the same subsystem.
	//
	// ⚠️ PARTLY DECODED. All we have ever read off it is the chosen option INDEX, which is the LAST u32 of the body
	// (it selects the branch; ConversationService.AdvanceConversation routes it). The bytes before it are a real,
	// undecoded region - the observed shape is `[u32 0xC350][u32 instance]`, but nothing pins that across the
	// corpus, and this refactor decodes nothing new. So they are carried as an explicit opaque `Prefix` rather than
	// being skipped, which keeps the round-trip exact and keeps the gap visible. Decoding it is Gitea #534.
	public static class KnubotAnswerCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.KnubotAnswerIIR;

		public static byte[] Build(KnubotAnswerIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteArray(value.Prefix).WriteUInt32(value.Index);

			return s.ToArray();
		}

		public static KnubotAnswerIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(KnubotAnswerCodec));
			if (r.Remaining < 4)
				throw CodecParseException.Underrun(nameof(KnubotAnswerCodec), r.Position, 4, r.Remaining);

			var prefix = r.ReadBytes(r.Remaining - 4).ToArray();
			var value = new KnubotAnswerIIR(prefix, r.ReadU32Be());
			r.EnsureAtEnd();

			return value;
		}
	}

	public sealed record KnubotAnswerIIR(byte[] Prefix, uint Index);
}
