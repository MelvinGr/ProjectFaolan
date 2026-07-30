using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Dialogue
{
	// c2s ScryMessageBoxAnswer (0x203B) - the answer to the scry confirmation box ScryShowMessageBoxCodec raises.
	// A pure ECHO plus the button index: `[u32 answer][u32 scriptThreadCtxId][u64 ctxCookie]`, and every captured
	// answer is 1 (confirm). So the ctxId + cookie are the correlation handle - whatever a sender puts in comes
	// straight back, which is what lets a pending action be resumed without server-side script threads.
	public static class ScryMessageBoxAnswerCodec
	{
		// Answer 1 = the confirm button; every captured answer is 1.
		public const uint Confirm = 1;
		public const ushort Opcode = (ushort)GameCharAgentOpcodes.ScryMessageBoxAnswer;

		public static byte[] Build(ScryMessageBoxAnswer value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteUInt32(value.Answer).WriteUInt32(value.ThreadCtxId).WriteUInt64(value.Cookie);

			return s.ToArray();
		}

		public static ScryMessageBoxAnswer Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(ScryMessageBoxAnswerCodec));
			var value = new ScryMessageBoxAnswer(r.ReadU32Be(), r.ReadU32Be(), r.ReadU64Be());
			r.EnsureAtEnd();

			return value;
		}
	}

	public sealed record ScryMessageBoxAnswer(uint Answer, uint ThreadCtxId, ulong Cookie);
}
