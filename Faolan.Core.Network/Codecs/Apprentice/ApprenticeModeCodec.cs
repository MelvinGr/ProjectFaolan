using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Apprentice
{
	// DispatchApprenticeModeOnOff(i_bOnOff) - GameCharInterface 0x81. The per-character mode flag: the client lights
	// its apprenticeship UI (the "Leave apprenticeship" entry, LDB 211711[3184]) while it is on. A single bool, which
	// this protocol spells as one byte - the same shape `DispatchSyncCanSprint` uses for its `i_bCanSprint`.
	public static class ApprenticeModeCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchApprenticeModeOnOff;

		public static byte[] Build(ApprenticeMode value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static byte[] Build(bool on)
		{
			return Build(new ApprenticeMode(on));
		}

		public static ApprenticeMode Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(ApprenticeModeCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static ApprenticeMode Read(ref SpanReader r)
		{
			return new ApprenticeMode(r.ReadU8() != 0);
		}

		internal static void Write(FaolanStream s, ApprenticeMode v)
		{
			s.WriteByte(v.On ? (byte)0x01 : (byte)0x00);
		}
	}

	public sealed record ApprenticeMode(bool On);
}
