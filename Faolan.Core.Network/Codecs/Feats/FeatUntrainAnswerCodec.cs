using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Feats
{
	// c2s FeatUntrainAnswer (GameCharAgentOpcodes 0x203E) - the player's reply to the untrain-all confirm dialog:
	//   [u8 answer][the 16-byte DispatchFeatUntrainMessageBox payload, echoed verbatim]
	// answer 0x01 = confirmed. The server matches the echoed token (the echo's 4th u32) to the outstanding request
	// (INetworkClientSession.PendingFeatUntrainToken) before resetting, which is what stops a stray or replayed
	// answer from wiping a feat sheet. See GameServerListener.Feats.cs / docs/subsystems/feats.md / #251.
	public static class FeatUntrainAnswerCodec
	{
		public const ushort Opcode = (ushort)GameCharAgentOpcodes.FeatUntrainAnswer;

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
