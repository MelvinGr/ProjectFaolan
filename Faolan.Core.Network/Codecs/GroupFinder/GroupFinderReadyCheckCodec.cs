using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.GroupFinder
{
	// s2c DispatchGroupFinderReadyCheck (0x20C6): the finder has a group for the activity and asks everyone to
	// confirm - the client raises the ready-check window and answers with c2s ReadyGroupFinder(bool, bool).
	// Captured as [00000026][00000000] (activity 38, second word 0; chat-annotated-4 frame 11925). The second
	// word varies in nothing we hold, so it is carried as-is and always sent 0.
	public static class GroupFinderReadyCheckCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchGroupFinderReadyCheck;

		public static byte[] Build(GroupFinderReadyCheck value)
		{
			using var s = new FaolanStream();
			s.WriteUInt32(value.ActivityId).WriteUInt32(value.Word);

			return s.ToArray();
		}

		public static byte[] Build(uint activityId, uint word = 0)
		{
			return Build(new GroupFinderReadyCheck(activityId, word));
		}

		public static GroupFinderReadyCheck Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GroupFinderReadyCheckCodec));
			var activityId = r.ReadU32Be();
			var word = r.ReadU32Be();
			r.EnsureAtEnd();

			return new GroupFinderReadyCheck(activityId, word);
		}
	}

	public sealed record GroupFinderReadyCheck(uint ActivityId, uint Word);
}
