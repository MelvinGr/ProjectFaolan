using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Apprentice
{
	/// <summary>
	///     <c>DispatchApprenticeModeOnOff(i_bOnOff)</c> - the per-character mode flag. The client lights its
	///     apprenticeship UI (the "Leave apprenticeship" entry, LDB 211711[3184]) while it is on.
	/// </summary>
	/// <remarks>
	///     A single bool, which this protocol spells as one byte - the same shape <c>DispatchSyncCanSprint</c> uses for
	///     its <c>i_bCanSprint</c>.
	/// </remarks>
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
