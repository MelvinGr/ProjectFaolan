using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.GroupFinder
{
	/// <summary>
	///     s2c <c>DispatchGroupFinderSignupSuccess</c> - the signup went through. The body is the
	///     <c>QueueGroupFinder</c> activity vector echoed back, then one byte.
	/// </summary>
	/// <remarks>
	///     Captured as the request's own three words with the trailing bools collapsed to a single 0 (chat-annotated-4
	///     frame 11923). What that byte means is unobserved beyond 0, so it is modelled as a flag and always sent 0.
	/// </remarks>
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
