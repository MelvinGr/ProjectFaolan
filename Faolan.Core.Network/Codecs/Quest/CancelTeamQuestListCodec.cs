using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Quest
{
	/// <summary>
	///     s2c DispatchCancelTeamQuestList - the answer to c2s RequestTeamQuestList. The body is
	///     <c>[u32 nQuestShareCode]</c>, one plain u32 read in the client's dispatcher
	///     (GameCharInterfaceDispatch_t::DispatchCancelTeamQuestList, FUN_004c5fa0).
	/// </summary>
	/// <remarks>
	///     This is the one quest-share message a capture performs. The client asks for the team quest list twice in
	///     login_2-clients_group-trade-friend (frames 1978 and 2851) and retail answers each within 250 ms carrying
	///     00000002 - the only two occurrences of the method in the whole corpus, one per request. The endpoints come with
	///     it: sender GameCharAgent instance 0x10, receiver GameCharInterface on the asking character, which is the pair the
	///     rest of the share dispatches assume by analogy.
	///     [V2: login_2-clients_group-trade-friend.tshark.json]
	///     <para>
	///         The code itself is not decoded: the client's handler is a signal emitter (FUN_0139cce0 to FUN_013998c0 to
	///         FUN_0044b1c0, a generic slot dispatcher) rather than a jump table, so there is no per-value branch to read a
	///         name off. <see cref="RetailNoList" /> therefore ships the observed constant rather than an invented meaning.
	///     </para>
	/// </remarks>
	public static class CancelTeamQuestListCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchCancelTeamQuestList;

		/// <summary>
		///     The share code retail replies with when the asking character has no team quest list. Both captured replies
		///     carry it, and no other value has been observed.
		/// </summary>
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
