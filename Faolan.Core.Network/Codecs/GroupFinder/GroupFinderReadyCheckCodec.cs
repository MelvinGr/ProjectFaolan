using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.GroupFinder
{
	/// <summary>
	///     s2c <c>DispatchGroupFinderReadyCheck</c> - the finder has a group for the activity and asks everyone to
	///     confirm. The client raises the ready-check window and answers with c2s <c>ReadyGroupFinder(bool, bool)</c>.
	/// </summary>
	/// <remarks>
	///     Captured as <c>[00000026][00000000]</c> (activity 38, second word 0; chat-annotated-4 frame 11925). The
	///     second word does not vary anywhere in the corpus, so it is carried as-is and always sent 0.
	/// </remarks>
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
