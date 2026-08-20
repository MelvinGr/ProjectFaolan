using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.GroupFinder
{
	// s2c DispatchGroupFinderSignupFailed (0x20C2): the finder refuses a QueueGroupFinder, with the reason as a
	// remoteformat the client resolves from its LDB. One u16-length string and nothing else. The captured refusal
	// (chat-annotated-4 frame 11781) carries category 10035 id 218378963 - "A team member has too many quests to
	// join the Group Finder." - and the identical queue body succeeds 30 s later once a quest is deleted, so the
	// precondition is the quest-log size, not anything in the request (#511).
	public static class GroupFinderSignupFailedCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchGroupFinderSignupFailed;

		// The captured refusal, verbatim (the TEAM form - retail sent it to a solo queuer too; the LDB carries a
		// solo twin "You have too many quests to queue for the Group Finder." under category 100 with the same id).
		public const string TooManyQuests =
			"<remoteformat id=\"218378963\" category=\"10035\" key=\"K.!S,L*lMmEZ3Y-4ohvZ\" knubot=\"0\"  ></remoteformat>";

		public static byte[] Build(GroupFinderSignupFailed value)
		{
			using var s = new FaolanStream();
			s.WriteString(value.Reason);

			return s.ToArray();
		}

		public static GroupFinderSignupFailed Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GroupFinderSignupFailedCodec));
			var reason = r.ReadStringU16Length();
			r.EnsureAtEnd();

			return new GroupFinderSignupFailed(reason);
		}
	}

	public sealed record GroupFinderSignupFailed(string Reason);
}
