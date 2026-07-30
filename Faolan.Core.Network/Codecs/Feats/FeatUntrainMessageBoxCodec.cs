using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Feats
{
	// s2c DispatchFeatUntrainMessageBox (GameCharInterfaceOpcodes 0x2064) - the feat untrain-all CONFIRM DIALOG.
	// A 16-byte body of 4x u32 BE: [kind][value][0][token]. In the hakotep untrain capture:
	// [1][0x001A3BDA][0][0x48018760]. `kind`=1 (untrain-all), `value` is likely the gold cost, `token` a transaction
	// id - the client renders the message box from these and ECHOES all 16 bytes back in FeatUntrainAnswer.
	//
	// The matching c2s reply is its own message and its own codec (FeatUntrainAnswerCodec), which composes this one
	// for the echoed payload. See GameServerListener.Feats.cs / docs/subsystems/feats.md / #251.
	public static class FeatUntrainMessageBoxCodec
	{
		public const ushort Opcode = (ushort)GameCharInterfaceOpcodes.DispatchFeatUntrainMessageBox;

		// The message box is a fixed 16 bytes; FeatUntrainAnswerCodec relies on that to frame its echo.
		public const int Length = 16;

		public static byte[] Build(FeatUntrainMessageBox value)
		{
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static byte[] Build(uint kind, uint value, uint token)
		{
			return Build(new FeatUntrainMessageBox(kind, value, token));
		}

		public static FeatUntrainMessageBox Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(FeatUntrainMessageBoxCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static FeatUntrainMessageBox Read(ref SpanReader r)
		{
			var kind = r.ReadU32Be();
			var value = r.ReadU32Be();
			r.ExpectU32Be(0u, "the message box's zero word");

			return new FeatUntrainMessageBox(kind, value, r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, FeatUntrainMessageBox v)
		{
			s.WriteUInt32(v.Kind).WriteUInt32(v.Value).WriteUInt32(0u).WriteUInt32(v.Token);
		}
	}

	public sealed record FeatUntrainMessageBox(uint Kind, uint Value, uint Token);
}
