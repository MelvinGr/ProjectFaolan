using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Quest
{
	/// <summary>
	///     s2c DispatchQuestShareGlobalFeedback - the answer to the sharer about the share as a whole:
	///     <c>[u32 feedbackType][u32 feedbackValue]</c>, two plain u32 reads in the client's dispatcher
	///     (GameCharInterfaceDispatch_t::DispatchQuestShareGlobalFeedback, FUN_004c58f0).
	/// </summary>
	/// <remarks>
	///     The type is a <see cref="QuestShareFeedback" />, and only that enum's global half rides this method: the client's
	///     handler is a jump table over 0..7 that logs "Invalid feedback type" for anything else. The value is a count for
	///     the two codes that have one - LegitTargets and OutOfRangeTargets, where the client picks a singular or plural LDB
	///     message by it - and unused for the rest. No capture performs a quest share. [V0]
	/// </remarks>
	public static class QuestShareGlobalFeedbackCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchQuestShareGlobalFeedback;

		public static byte[] Build(QuestShareGlobalFeedback value)
		{
			using var s = new FaolanStream();
			s.WriteUInt32((uint)value.Type).WriteUInt32(value.Value);

			return s.ToArray();
		}

		public static byte[] Build(QuestShareFeedback type, uint value = 0)
		{
			return Build(new QuestShareGlobalFeedback(type, value));
		}

		public static QuestShareGlobalFeedback Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(QuestShareGlobalFeedbackCodec));
			var type = (QuestShareFeedback)r.ReadU32Be();
			var value = r.ReadU32Be();
			r.EnsureAtEnd();

			return new QuestShareGlobalFeedback(type, value);
		}
	}

	public sealed record QuestShareGlobalFeedback(QuestShareFeedback Type, uint Value);
}
