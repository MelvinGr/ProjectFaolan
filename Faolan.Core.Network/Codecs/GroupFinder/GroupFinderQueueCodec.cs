using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.GroupFinder
{
	/// <summary>
	///     c2s <c>QueueGroupFinder(const std::vector&lt;uint32&gt;&amp; activites, bool wantsLeader, bool
	///     privateTeam)</c> - the player signs up for a group-finder activity.
	/// </summary>
	/// <remarks>
	///     The captured body is a count-framed vector of three u32 words followed by the two bools
	///     (chat-annotated-4, frames 11778/11911, byte-identical before and after the "too many quests" refusal, which
	///     is what shows the refusal to be server-side state rather than request content).
	///     <para>
	///         The three words are a bitmask over the activity catalog (RDB 1000627): the captured signup sets bit 37
	///         of the 96-bit field, and the activity the rest of the flow names is 38, so bit N marks activity id N+1
	///         checked in the finder window. <c>privateTeam</c> is the capture's annotated "entered as private team".
	///     </para>
	/// </remarks>
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
		/// <summary>
		///     The 1-based activity ids whose bits are set across the words: word 0 carries bits 0..31 (activities
		///     1..32), word 1 the next 32, and so on.
		/// </summary>
		public IEnumerable<uint> ActivityIds =>
			ActivityWords.SelectMany((word, wordIndex) => Enumerable.Range(0, 32)
				.Where(bit => (word & 1u << bit) != 0)
				.Select(bit => (uint)(wordIndex * 32 + bit + 1)));
	}
}
