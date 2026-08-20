using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.GroupFinder
{
	// s2c DispatchGroupFinderReceivedRotatingActivityChildInfo (0x20CF): the answer to c2s
	// RequestRotatingActivityChildInfo(uint32 activityId) - which child of a ROTATING activity is running now, and
	// which one is up next. The finder window asks it once per session for activity 1 and paints the "this week /
	// next week" line from the reply.
	//
	//   [u32 activityId][u32 currentChildIndex][u32 nextChildIndex]
	//
	// ⭐ THE FIELD SPLIT COMES FROM THE CLIENT, NOT FROM THE BYTES. All five captured replies are byte-identical
	// (`00000001 00000002 00000003`, two captures), so nothing in the corpus separates three u32s from one u32 and
	// a u64, or from a list. What settles it is the client's own recovered field list for this dispatch -
	// `activityId, currentChildIndex, nextChildIndex` [V1: docs/re/aoc-client-rpc-methods.json] - three fields
	// against a 12-byte body, which is three u32s and can be nothing else.
	//
	// ⚠️ What the corpus does NOT pin is what makes a child current: every capture in it was taken in one sitting,
	// so `2` and `3` are one moment of retail's rotation schedule, not a measured relationship to `1`. We model no
	// rotation, so `GroupFinderService` answers with that captured pair. Gitea #882.
	public static class RotatingActivityChildInfoCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchGroupFinderReceivedRotatingActivityChildInfo;

		public static byte[] Build(RotatingActivityChildInfo value)
		{
			ArgumentNullException.ThrowIfNull(value);

			using var s = new FaolanStream();
			s.WriteUInt32(value.ActivityId).WriteUInt32(value.CurrentChildIndex).WriteUInt32(value.NextChildIndex);

			return s.ToArray();
		}

		public static byte[] Build(uint activityId, uint currentChildIndex, uint nextChildIndex)
		{
			return Build(new RotatingActivityChildInfo(activityId, currentChildIndex, nextChildIndex));
		}

		public static RotatingActivityChildInfo Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(RotatingActivityChildInfoCodec));
			var activityId = r.ReadU32Be();
			var currentChildIndex = r.ReadU32Be();
			var nextChildIndex = r.ReadU32Be();
			r.EnsureAtEnd();

			return new RotatingActivityChildInfo(activityId, currentChildIndex, nextChildIndex);
		}
	}

	/// <summary>The rotating parent activity the client asked about, and which of its children is up.</summary>
	public sealed record RotatingActivityChildInfo(uint ActivityId, uint CurrentChildIndex, uint NextChildIndex);
}
