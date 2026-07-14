using System;

namespace Faolan.Core.Network.Codecs.Feats
{
	// The feat untrain-all CONFIRM DIALOG. Two matched messages (decoded from the hakotep untrain capture):
	//   - s2c DispatchFeatUntrainMessageBox (GameCharInterfaceOpcodes 0x2064): a 16-byte body of 4x u32 BE
	//     [kind][value][0][token]. In the capture: [1][0x001A3BDA][0][0x48018760]. `kind`=1 (untrain-all), `value`
	//     is likely the gold cost, `token` a transaction id - the client renders the message box from these and
	//     ECHOES all 16 bytes back.
	//   - c2s FeatUntrainAnswer (GameCharAgentOpcodes 0x203E): [u8 answer][the 16-byte payload echoed]. answer 0x01
	//     = confirmed. The server matches the echoed token to the outstanding request before resetting.
	// See GameServerListener.Feats.cs / docs/subsystems/feats.md / #251.
	public static class FeatUntrainMessageBoxCodec
	{
		public const byte AnswerConfirm = 0x01;
		public const int PayloadLength = 16;

		public static byte[] Build(uint kind, uint value, uint token)
		{
			return new FaolanStream()
				.WriteUInt32(kind)
				.WriteUInt32(value)
				.WriteUInt32(0u)
				.WriteUInt32(token)
				.ToArray();
		}

		// Parse a FeatUntrainAnswer body: the leading answer byte + the 16-byte echoed payload. `token` is the 4th
		// u32 of the echo (matched against INetworkClientSession.PendingFeatUntrainToken).
		public static bool TryParseAnswer(byte[] body, out byte answer, out uint token)
		{
			answer = 0;
			token = 0;
			if (body == null || body.Length < 1 + PayloadLength)
				return false;

			answer = body[0];
			token = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(1 + 12, 4));
			return true;
		}
	}
}
