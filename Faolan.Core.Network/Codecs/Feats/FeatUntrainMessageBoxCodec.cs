using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Feats
{
	/// <summary>
	///     s2c <c>DispatchFeatUntrainMessageBox</c> - the feat untrain-all confirm dialog. A 16-byte body of four
	///     big-endian u32: <c>[kind][value][0][token]</c>.
	/// </summary>
	/// <remarks>
	///     The hakotep untrain capture carries <c>[1][0x001A3BDA][0][0x48018760]</c>: kind 1 is untrain-all,
	///     <c>value</c> is most likely the gold cost and <c>token</c> a transaction id. The client renders the message
	///     box from these and echoes all 16 bytes back in <c>FeatUntrainAnswer</c>, whose codec composes this one for
	///     the echoed payload. See docs/subsystems/feats.md.
	/// </remarks>
	public static class FeatUntrainMessageBoxCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchFeatUntrainMessageBox;

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
