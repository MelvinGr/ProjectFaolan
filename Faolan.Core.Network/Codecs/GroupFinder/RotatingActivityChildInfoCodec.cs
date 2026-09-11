using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.GroupFinder
{
	/// <summary>
	///     s2c <c>DispatchGroupFinderReceivedRotatingActivityChildInfo</c> - the answer to c2s
	///     <c>RequestRotatingActivityChildInfo(activityId)</c>: which child of a rotating activity is running now and
	///     which is up next, as <c>[u32 activityId][u32 currentChildIndex][u32 nextChildIndex]</c>. The finder window
	///     asks it once per session for activity 1 and paints the "this week / next week" line from the reply.
	/// </summary>
	/// <remarks>
	///     The field split comes from the client rather than from the bytes: all five captured replies are
	///     byte-identical (<c>00000001 00000002 00000003</c>, two captures), so nothing in the corpus separates three
	///     u32s from one u32 and a u64, or from a list. The client's own recovered field list for this dispatch -
	///     <c>activityId, currentChildIndex, nextChildIndex</c> [V1: docs/re/aoc-client-rpc-methods.json] - is three
	///     fields against a 12-byte body, which can only be three u32s.
	///     <para>
	///         What the corpus does not pin is what makes a child current: every capture was taken in one sitting, so
	///         <c>2</c> and <c>3</c> are one moment of retail's rotation schedule rather than a measured relationship
	///         to <c>1</c>. No rotation is modelled, so <c>GroupFinderService</c> answers with that captured pair.
	///         Open work tracked in Gitea #882.
	///     </para>
	/// </remarks>
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
