using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.GroupFinder
{
	// s2c DispatchGroupFinderReadyCheckStatus (0x20C9): the ready-check tally after a member answers. Captured as
	// [00000001][00000000][00000001] right after the solo queuer's ReadyGroupFinder(true) (chat-annotated-4 frame
	// 11943), which reads as one ready, none declined, one expected - a solo private team confirms itself. The
	// middle word never varies in the corpus; the field names are that reading, not a client-verified layout.
	public static class GroupFinderReadyCheckStatusCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchGroupFinderReadyCheckStatus;

		public static byte[] Build(GroupFinderReadyCheckStatus value)
		{
			using var s = new FaolanStream();
			s.WriteUInt32(value.Ready).WriteUInt32(value.NotReady).WriteUInt32(value.Expected);

			return s.ToArray();
		}

		public static byte[] Build(uint ready, uint notReady, uint expected)
		{
			return Build(new GroupFinderReadyCheckStatus(ready, notReady, expected));
		}

		public static GroupFinderReadyCheckStatus Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GroupFinderReadyCheckStatusCodec));
			var ready = r.ReadU32Be();
			var notReady = r.ReadU32Be();
			var expected = r.ReadU32Be();
			r.EnsureAtEnd();

			return new GroupFinderReadyCheckStatus(ready, notReady, expected);
		}
	}

	public sealed record GroupFinderReadyCheckStatus(uint Ready, uint NotReady, uint Expected);
}
