using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Quest
{
	// s2c DispatchCancelTeamQuestList (GameCharInterface 0x204C) - the answer to c2s RequestTeamQuestList (0x2054):
	// `[u32 nQuestShareCode]`, one plain u32 read in the client's dispatcher
	// (GameCharInterfaceDispatch_t::DispatchCancelTeamQuestList, FUN_004c5fa0).
	//
	// ⚠️ This is the ONE quest-share message a capture performs. The client asks for the team quest list twice in
	// `login_2-clients_group-trade-friend` (frames 1978 and 2851), and retail answers each within 250 ms with this
	// method carrying `00000002` - the only two occurrences of 0x204C in the whole corpus, one per request. The
	// endpoints are observed with it: sender `GameCharAgent` instance 0x10, receiver `GameCharInterface` on the
	// asking character, which is the pair the rest of the share dispatches assume by analogy.
	// [V2: login_2-clients_group-trade-friend.tshark.json]
	//
	// The CODE is not decoded - the client's handler is a signal emitter (FUN_0139cce0 -> FUN_013998c0 ->
	// FUN_0044b1c0, a generic slot dispatcher), not a jump table, so unlike the two feedback methods there is no
	// per-value branch in the binary to read a name off. What we have is retail's own answer when there is no team
	// quest list to send, which is the only situation we are ever in - so `RetailNoList` ships the observed constant
	// rather than a meaning we would be inventing.
	public static class CancelTeamQuestListCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchCancelTeamQuestList;

		// The share code retail replies with when the asking character has no team quest list. Both captured replies
		// carry it; no other value has ever been observed.
		public const uint RetailNoList = 2;

		public static byte[] Build(CancelTeamQuestList value)
		{
			using var s = new FaolanStream();
			s.WriteUInt32(value.QuestShareCode);

			return s.ToArray();
		}

		public static byte[] Build(uint questShareCode)
		{
			return Build(new CancelTeamQuestList(questShareCode));
		}

		public static CancelTeamQuestList Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(CancelTeamQuestListCodec));
			var code = r.ReadU32Be();
			r.EnsureAtEnd();

			return new CancelTeamQuestList(code);
		}
	}

	public sealed record CancelTeamQuestList(uint QuestShareCode);
}
