using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.GroupFinder
{
	// s2c DispatchGroupFinderSignupSuccess (0x20C4): the signup went through. The body is the QueueGroupFinder
	// activity vector echoed back, then one byte - captured as [0x0FC4][00000000][00000020][00000000][00], i.e.
	// the request's own three words with the trailing bools collapsed to a single 0 (chat-annotated-4 frame 11923).
	// What that byte means is unobserved beyond 0; it is modelled as a flag and always sent 0.
	public static class GroupFinderSignupSuccessCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchGroupFinderSignupSuccess;

		public static byte[] Build(GroupFinderSignupSuccess value)
		{
			using var s = new FaolanStream();
			s.WriteUInt32(ListFrame.ForCount(value.ActivityWords.Count));
			foreach (var word in value.ActivityWords)
				s.WriteUInt32(word);
			s.WriteByte(value.Flag ? (byte)1 : (byte)0);

			return s.ToArray();
		}

		public static GroupFinderSignupSuccess Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GroupFinderSignupSuccessCodec));
			var count = r.ReadListCount();
			var words = new uint[count];
			for (var i = 0; i < count; i++)
				words[i] = r.ReadU32Be();
			var flag = r.ReadU8() != 0;
			r.EnsureAtEnd();

			return new GroupFinderSignupSuccess(words, flag);
		}
	}

	public sealed record GroupFinderSignupSuccess(IReadOnlyList<uint> ActivityWords, bool Flag);
}
