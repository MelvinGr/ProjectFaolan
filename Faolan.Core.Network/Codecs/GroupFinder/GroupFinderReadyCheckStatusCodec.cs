using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.GroupFinder
{
	/// <summary>
	///     s2c <c>DispatchGroupFinderReadyCheckStatus</c> - the ready-check tally after a member answers.
	/// </summary>
	/// <remarks>
	///     Captured as <c>[00000001][00000000][00000001]</c> right after the solo queuer's
	///     <c>ReadyGroupFinder(true)</c> (chat-annotated-4 frame 11943), which reads as one ready, none declined, one
	///     expected - a solo private team confirming itself. The middle word never varies in the corpus, and the field
	///     names are that reading rather than a client-verified layout.
	/// </remarks>
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
