using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Team;

namespace Faolan.Core.Network.Codecs.Quest
{
	// s2c DispatchQuestShareRequest (GameCharInterface 0x204A) - "X wants to share <quest> with you", the message that
	// raises the client's quest-share window (`Views/QuestShareGUI/QuestShareView.xml`, message key
	// "QuestSharingRequest" in LDB category 300).
	//
	//   [ID32 sharer][u32 startTaskTemplate][string questName]
	//
	// The field order and widths come from the client's reader, GameCharInterfaceDispatch_t::DispatchQuestShareRequest
	// (FUN_004c5bf0): TWO u32 reads into one 8-byte ID32, one u32, then the string primitive - each guarded by its own
	// "stream bad after 'i_cCharID' / 'i_nStartTaskTemplate' / 'i_cQuestName'" bail-out, which is what names them.
	//
	// The sharer's ID32 is an ordinary character reference (class 0xC350): the window's tick handler compares its
	// first word against 50000 before resolving the sharer to measure the distance, so a different class simply
	// stops it tracking.
	//
	// ⚠️ No capture performs a quest share, so this is built from the client rather than observed - there is nothing
	// to gate it byte-exactly against. [V0]
	public static class QuestShareRequestCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchQuestShareRequest;

		public static byte[] Build(QuestShareRequest value)
		{
			using var s = new FaolanStream();
			EntityIdCodec.Write(s, value.Sharer);
			s.WriteUInt32(value.StartTaskTemplate).WriteString(value.QuestName);

			return s.ToArray();
		}

		public static byte[] Build(uint sharerId, uint startTaskTemplate, string questName)
		{
			return Build(new QuestShareRequest(EntityIds.Character(sharerId), startTaskTemplate, questName));
		}

		public static QuestShareRequest Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(QuestShareRequestCodec));
			var sharer = EntityIdCodec.Read(ref r);
			var startTaskTemplate = r.ReadU32Be();
			var questName = r.ReadStringU16Length();
			r.EnsureAtEnd();

			return new QuestShareRequest(sharer, startTaskTemplate, questName);
		}
	}

	// StartTaskTemplate is the quest id the accepting client hands straight back in c2s ShareQuestAccepted: the
	// window builder (ControlCenterModule_c::SlotQuestShareRequest, FUN_01220630) keeps only the sharer's ID32 and
	// binds this word as the accept button's 4-byte action payload, and ShareQuestAccepted's proxy writes exactly one
	// byte-swapped u32 out of its `InstanceID_t`. So the accept echoes this number and carries nothing else.
	public sealed record QuestShareRequest(EntityId Sharer, uint StartTaskTemplate, string QuestName);
}
