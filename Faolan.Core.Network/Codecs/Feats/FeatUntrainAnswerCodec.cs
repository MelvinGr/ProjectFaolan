using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Feats
{
	/// <summary>
	///     c2s <c>FeatUntrainAnswer</c> - the player's reply to the untrain-all confirm dialog:
	///     <c>[u8 answer][the 16-byte DispatchFeatUntrainMessageBox payload, echoed verbatim]</c>.
	/// </summary>
	/// <remarks>
	///     Answer 0x01 is confirmed. The server matches the echoed token against the outstanding request
	///     (<c>INetworkClientSession.PendingFeatUntrainToken</c>) before resetting, which is what stops a stray or
	///     replayed answer from wiping a feat sheet. See docs/subsystems/feats.md.
	/// </remarks>
	public static class FeatUntrainAnswerCodec
	{
		public const uint Method = (uint)GameCharAgentMethods.FeatUntrainAnswer;

		public const byte AnswerConfirm = 0x01;

		public static byte[] Build(FeatUntrainAnswer value)
		{
			using var s = new FaolanStream();
			s.WriteByte(value.Answer);
			FeatUntrainMessageBoxCodec.Write(s, value.Echo);

			return s.ToArray();
		}

		public static FeatUntrainAnswer Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(FeatUntrainAnswerCodec));
			var answer = r.ReadU8();
			var echo = FeatUntrainMessageBoxCodec.Read(ref r);
			r.EnsureAtEnd();

			return new FeatUntrainAnswer(answer, echo);
		}
	}

	public sealed record FeatUntrainAnswer(byte Answer, FeatUntrainMessageBox Echo);
}
