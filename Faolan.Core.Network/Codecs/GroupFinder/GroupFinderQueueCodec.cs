using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.GroupFinder
{
	// c2s QueueGroupFinder (0x207A): the player signs up for a group-finder activity. The client's own signature is
	//   QueueGroupFinder(const std::vector<uint32>& activites, bool wantsLeader, bool privateTeam)
	// and the captured body reads exactly that: a count-framed vector of THREE u32 words, then the two bools -
	//   [0x0FC4][00000000][00000020][00000000][00][01]
	// (chat-annotated-4, frames 11778/11911 - byte-identical before and after the "too many quests" refusal, which
	// is how we know the refusal is server-side state, not request content). The three words are a BITMASK over the
	// activity catalog (RDB 1000627): bit 37 across the 96-bit field is set, and the activity the rest of the flow
	// names (RequestNeededRole / DispatchGroupFinderReadyCheck) is 38 - so bit N marks activity id N+1 checked in
	// the finder window. `privateTeam` is the capture's annotated "entered as private team".
	public static class GroupFinderQueueCodec
	{
		public const uint Method = (uint)CSPlayerAgentMethods.QueueGroupFinder;

		public static byte[] Build(QueueGroupFinderRequest value)
		{
			using var s = new FaolanStream();
			s.WriteUInt32(ListFrame.ForCount(value.ActivityWords.Count));
			foreach (var word in value.ActivityWords)
				s.WriteUInt32(word);
			s.WriteByte(value.WantsLeader ? (byte)1 : (byte)0);
			s.WriteByte(value.PrivateTeam ? (byte)1 : (byte)0);

			return s.ToArray();
		}

		public static QueueGroupFinderRequest Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GroupFinderQueueCodec));
			var count = r.ReadListCount();
			var words = new uint[count];
			for (var i = 0; i < count; i++)
				words[i] = r.ReadU32Be();
			var wantsLeader = r.ReadU8() != 0;
			var privateTeam = r.ReadU8() != 0;
			r.EnsureAtEnd();

			return new QueueGroupFinderRequest(words, wantsLeader, privateTeam);
		}
	}

	public sealed record QueueGroupFinderRequest(IReadOnlyList<uint> ActivityWords, bool WantsLeader, bool PrivateTeam)
	{
		// The 1-based activity ids whose bits are set across the words (word 0 carries bits 0..31 = activities
		// 1..32, word 1 the next 32, ...). The captured signup's single set bit (word 1, bit 5 = activity 38) is
		// the id every per-activity message in the same flow carries.
		public IEnumerable<uint> ActivityIds =>
			ActivityWords.SelectMany((word, wordIndex) => Enumerable.Range(0, 32)
				.Where(bit => (word & 1u << bit) != 0)
				.Select(bit => (uint)(wordIndex * 32 + bit + 1)));
	}
}
