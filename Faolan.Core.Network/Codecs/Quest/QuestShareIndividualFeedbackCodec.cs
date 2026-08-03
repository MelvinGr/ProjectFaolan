using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Quest
{
	// s2c DispatchQuestShareIndividualFeedback (GameCharInterface 0x2049) - the answer to the SHARER about ONE
	// team-mate: `[u32 feedbackType][string playerName]`.
	//
	// ⚠️ The second field is a wire STRING, not an ID32, even though the client's signature calls it `i_cFeedbackValue`
	// and the `i_c` prefix normally marks an object. The dispatcher (GameCharInterfaceDispatch_t::
	// DispatchQuestShareIndividualFeedback, FUN_004c5a50) reads it with the string primitive FUN_0048f840 - the same
	// call DispatchQuestShareRequest uses for `i_cQuestName` - and never the id reader. The handler then substitutes
	// it into the LDB message as the name parameter, which is what it is for.
	//
	// Only QuestShareFeedback's INDIVIDUAL half rides this method; the client's jump table accepts 3,4,8,9,10,11 and
	// logs "Invalid feedback type" for the rest.
	//
	// ⚠️ No capture performs a quest share. [V0]
	public static class QuestShareIndividualFeedbackCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchQuestShareIndividualFeedback;

		public static byte[] Build(QuestShareIndividualFeedback value)
		{
			using var s = new FaolanStream();
			s.WriteUInt32((uint)value.Type).WriteString(value.PlayerName);

			return s.ToArray();
		}

		public static byte[] Build(QuestShareFeedback type, string playerName)
		{
			return Build(new QuestShareIndividualFeedback(type, playerName));
		}

		public static QuestShareIndividualFeedback Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(QuestShareIndividualFeedbackCodec));
			var type = (QuestShareFeedback)r.ReadU32Be();
			var playerName = r.ReadStringU16Length();
			r.EnsureAtEnd();

			return new QuestShareIndividualFeedback(type, playerName);
		}
	}

	public sealed record QuestShareIndividualFeedback(QuestShareFeedback Type, string PlayerName);
}
