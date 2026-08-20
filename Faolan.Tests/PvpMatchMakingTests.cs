using System;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Pvp;
using Xunit;

namespace Faolan.Tests
{
	// The PvP minigame QUEUE (Gitea #882), pinned against every body the corpus carries for it. The oracle is
	// `--report method-bodies --report-arg PvPMatchMaking` over the two group-finder captures plus the four login
	// dumps that carry the empty board.
	//
	// ⭐ The point of this file is that the board MOVES. Before #882's capture the corpus only had the empty
	// `[0x3F1][u32 0]` from four login dumps, and the server shipped that as a constant. These 43 bodies are one
	// session's worth of the same message tracking the player's own signups second by second - which is what turns
	// "captured empty" into a three-level map with a live count in it.
	public class PvpMatchMakingTests
	{
		// c2s 0x2052 PvPMatchMakingPutIntoMatchMaking - all 5 distinct bodies of the 8 captured signups.
		public static TheoryData<string, uint, uint, bool, uint> CapturedSignups => new()
		{
			// map,                          type, bringTeam, queueType
			{ "00001451000000050000000000", 5201, 5, false, 0 }, // Blood Ravine, Capture The Skull (x3)
			{ "0000138E000000040000000000", 5006, 4, false, 0 }, // The Call of Jhebbal Sag, Conquest (x2)
			{ "00001394000000050000000000", 5012, 5, false, 0 }, // Lost Temple
			{ "0000138D000000050000000000", 5005, 5, false, 0 }, // Hallowed Vaults
			{ "00000000000000000000000000", 0, 0, false, 0 } // the "join first available" checkbox
		};

		// s2c 0x2080 DispatchPvPMatchMakingYouAreInMatchMaking - the ack each of those got, in the same order.
		public static TheoryData<string, string> CapturedSignupAndAck => new()
		{
			{ "00001451000000050000000000", "0000145100000005010000000000" },
			{ "0000138E000000040000000000", "0000138E00000004010000000000" },
			{ "00001394000000050000000000", "0000139400000005010000000000" },
			{ "0000138D000000050000000000", "0000138D00000005010000000000" },
			{ "00000000000000000000000000", "0000000000000000010000000000" }
		};

		// s2c 0x2092 DispatchPvPMatchMakingInfoRequestReturn - all 10 distinct bodies of the 43 captured boards,
		// plus the empty one the four login dumps carry.
		public static TheoryData<string> CapturedBoards => new()
		{
			"000003F100000000",
			"000007E20000000500000BD30000138D000007E2000000000000000000001451000007E2000000000000000100000000",
			"000007E20000000500000BD30000138D000007E2000000000000000000001451000007E2000000000000000200000000",
			"00000BD300000004000007E20000138E000007E200000000000000000000000500000BD30000138D000007E2000000000000000000001451000007E2000000000000000100000000",
			"00000BD300000004000007E20000138E000007E200000000000000000000000500000BD30000138D000007E2000000000000000000001451000007E2000000000000000200000000",
			"00000BD300000004000007E20000138E000007E200000000000000010000000500000BD30000138D000007E2000000000000000000001451000007E2000000000000000100000000",
			"00000BD300000004000007E20000138E000007E200000000000000000000000500000FC40000138D000007E2000000000000000000001394000007E2000000000000000000001451000007E2000000000000000100000000",
			"00000BD300000004000007E20000138E000007E200000000000000000000000500000FC40000138D000007E2000000000000000000001394000007E2000000000000000000001451000007E2000000000000000100000001",
			"00000BD300000004000007E20000138E000007E200000000000000000000000500000FC40000138D000007E2000000000000000000001394000007E2000000000000000000001451000007E2000000000000000200000000",
			"00000BD300000004000007E20000138E000007E200000000000000000000000500000FC40000138D000007E2000000000000000000001394000007E2000000000000000100001451000007E2000000000000000100000000",
			"00000BD300000004000007E20000138E000007E200000000000000000000000500000FC40000138D000007E2000000000000000100001394000007E2000000000000000000001451000007E2000000000000000100000000"
		};

		[Theory]
		[MemberData(nameof(CapturedSignups))]
		public void Signup_CarvesTheClientsFourFieldsAndRebuilds(string hex, uint mapId, uint minigameType, bool bringTeam, uint queueType)
		{
			var captured = Convert.FromHexString(hex);

			var parsed = PvpMatchMakingSignupCodec.Parse(captured);

			Assert.Equal(mapId, parsed.MapId);
			Assert.Equal(minigameType, parsed.MinigameType);
			Assert.Equal(bringTeam, parsed.BringTeam);
			Assert.Equal(queueType, parsed.QueueType);
			Assert.Equal(captured, PvpMatchMakingSignupCodec.Build(parsed));
		}

		// ⭐ THE ACK IS THE SIGNUP, ECHOED. Building it from the request alone reproduces all five captured replies
		// byte for byte, which is what says the server adds only the two flags - and it is the whole of what
		// `PvpMatchMakingService` has to construct.
		[Theory]
		[MemberData(nameof(CapturedSignupAndAck))]
		public void Ack_IsTheSignupEchoedWithShowFifoSet(string signupHex, string ackHex)
		{
			var capturedAck = Convert.FromHexString(ackHex);
			var signup = PvpMatchMakingSignupCodec.Parse(Convert.FromHexString(signupHex));

			Assert.Equal(capturedAck, PvpMatchMakingStatusCodec.Build(signup, false));

			var parsed = PvpMatchMakingStatusCodec.Parse(capturedAck);
			Assert.True(parsed.ShowFifo); // 1 in all eight captured acks
			Assert.False(parsed.AsTeamMember); // 0 in all eight - the capture was solo throughout
			Assert.Equal(signup.MapId, parsed.MapId);
			Assert.Equal(signup.MinigameType, parsed.MinigameType);
			Assert.Equal(signup.QueueType, parsed.QueueType);
			Assert.Equal(capturedAck, PvpMatchMakingStatusCodec.Build(parsed));
		}

		[Theory]
		[MemberData(nameof(CapturedBoards))]
		public void Board_ParsesToThePacketEndAndRebuilds(string hex)
		{
			var captured = Convert.FromHexString(hex);

			var parsed = PvpMatchMakingInfoCodec.Parse(captured);

			Assert.Equal(captured, PvpMatchMakingInfoCodec.Build(parsed));
		}

		// The empty board is not a special case in the codec - it is `Empty` going through the same writer, which is
		// what makes the four login captures and the 43 live ones one grammar rather than a constant plus a grammar.
		[Fact]
		public void Board_EmptyIsTheLoginCapturesBody()
		{
			Assert.Equal(Convert.FromHexString("000003F100000000"), PvpMatchMakingInfoCodec.Build(PvpMatchMakingInfo.Empty));
		}

		// ⚠️ `0x7E2` is the ONE-ELEMENT list frame, not a constant. Every innermost frame in the corpus is `0x7E2`
		// because every captured signup carries queueType 0, so reading it as a constant would have looked right on
		// all 43 bodies and hard-coded the single-queue case. This asserts the codec writes the frame from the count.
		[Fact]
		public void Board_FramesEveryLevelFromItsOwnCount()
		{
			var twoQueues = new PvpMatchMakingInfo(
				[new PvpMatchMakingTypeBoard(5, [new PvpMatchMakingMapBoard(5201, [new PvpMatchMakingQueue(0, 3), new PvpMatchMakingQueue(1, 4)])])], 0);

			var round = PvpMatchMakingInfoCodec.Parse(PvpMatchMakingInfoCodec.Build(twoQueues));

			Assert.Equal(2, round.Types[0].Maps[0].Queues.Count);
			Assert.Equal([3u, 4u], round.Types[0].Maps[0].Queues.Select(q => q.Signups));
		}

		// ⭐ The board CONSTRUCTED from a live registry plus live signups is retail's, byte for byte. This is the one
		// that would fail if the sticky registry, the ordering or the count placement were wrong: the capture's
		// `The Call of Jhebbal Sag` still appears at ZERO here, long after the player dequeued it.
		[Fact]
		public void Board_BuiltFromLiveSignupsIsTheCapturedBody()
		{
			(uint Type, uint Map, uint Queue)[] known = [(4, 5006, 0), (5, 5005, 0), (5, 5201, 0)];
			(uint Type, uint Map, uint Queue)[] held = [(4, 5006, 0), (5, 5201, 0)];

			var board = PvpMatchMakingBoard.From(known, held, 0);

			Assert.Equal(
				Convert.FromHexString(
					"00000BD300000004000007E20000138E000007E200000000000000010000000500000BD30000138D000007E2000000000000000000001451000007E2000000000000000100000000"),
				PvpMatchMakingInfoCodec.Build(board));
		}

		// The "join first available" signups are counted in `i_nNumberOfAnyApplies` and are deliberately NOT listed
		// as a type-0 group: the capture's board grows no such group when the player checks that box, and the
		// annotations name both edges ("selected join first avaialble instead" / "disabled join first avaialbl").
		[Fact]
		public void Board_JoinFirstAvailableCountsAsAnAnyApplyAndNotAsAGroup()
		{
			(uint Type, uint Map, uint Queue)[] known = [(4, 5006, 0), (5, 5005, 0), (5, 5012, 0), (5, 5201, 0)];

			var board = PvpMatchMakingBoard.From(known, [(5, 5201, 0)], 1);

			Assert.DoesNotContain(board.Types, t => t.MinigameType == 0);
			Assert.Equal(1u, board.NumberOfAnyApplies);
			Assert.Equal(
				Convert.FromHexString(
					"00000BD300000004000007E20000138E000007E200000000000000000000000500000FC40000138D000007E2000000000000000000001394000007E2000000000000000000001451000007E2000000000000000100000001"),
				PvpMatchMakingInfoCodec.Build(board));
		}

		// The signup a `(0, 0)` body carries is the checkbox, not map 0 - `PvpMatchMakingService` routes on this.
		[Fact]
		public void Signup_MapAndTypeZeroIsTheJoinFirstAvailableCheckbox()
		{
			Assert.True(PvpMatchMakingSignupCodec.Parse(Convert.FromHexString("00000000000000000000000000")).IsAnyAvailable);
			Assert.False(PvpMatchMakingSignupCodec.Parse(Convert.FromHexString("00001451000000050000000000")).IsAnyAvailable);
		}

		// The four captured removals are each answered by an EMPTY 0x2081 ~0.3 s later, 1:1 with no exceptions -
		// which is the whole of what that no-arg slot is known to be (see the Reserved0x81 note in Opcodes.cs).
		[Fact]
		public void RemoveFromMatchMaking_IsAnsweredByTheEmptyNoArgSlot()
		{
			Assert.Equal(0x81, (int)CSPlayerInterfaceMethods.Reserved0x81);
			Assert.Equal(0x53, (int)CSPlayerAgentMethods.PvPMatchMakingRemoveFromMatchMaking);
		}
	}
}
