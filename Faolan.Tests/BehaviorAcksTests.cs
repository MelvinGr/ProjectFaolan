using System;
using System.Linq;
using Faolan.Core.Extensions;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Protobuf;
using Xunit;

namespace Faolan.Tests
{
	// Golden tests for the BehaviorControlCenter echo-acks, byte-exact against the armed+unarmed combat capture
	// (Captures/Stihetfv_soldier-conqueror_login_combat_armed-and-unarmed.pcapng, 2026-06-12). Each case is a real
	// client request (UP) paired with retail's reply (DOWN); request/reply hex includes the leading u32 body length
	// (the builders take the length-prefixed request and return the reply content without its length prefix).
	public class BehaviorAcksTests
	{
		private const uint Instance = 0x021663DD; // the captured character

		// The captured Broken Oar (item 23246) identity quad, as carried by the equip ack (= Items.Data1..4).
		private static readonly Item Oar = new()
		{
			Id = 23246,
			Key = new ProtoDecryptionKey
			{
				Key1 = 0x8B2978E3,
				Key2 = 0xAE53830B,
				Key3 = 0x0D5B7D66,
				Key4 = 0x2985A0FE
			}
		};

		[Fact]
		public void CastInterruptAbort_MatchesCapture() // NecroL80_..._cast-interupt idx988 begin + idx1006 cancel -> idx1013
		{
			// A ground-target Deadly Blizzard cast, interrupted by movement: the client sends the cancel (state=2) and
			// retail answers with the state=0x0007 abort that drops the cast bar + pops "Interrupted". The abort carries
			// the begin's args with phase=Mode(2) spliced in, at wire state 7, with the cancel's tail (last byte cleared).
			// Only the begin's ARGS block feed the builder; behaviorId + tail come from the cancel packet.
			var beginArgs = Hex("08c1e80a1002280a3000609a08"); // idx988 args (13 bytes)
			var cancel = Hex("0000004a642cd3d60000c3500802e5d40002000000040000001208c1e80a1002180220e45f280a3000609a080000002d0002348e004378e0e141bfe148436ed78c00000000032047a70000041a01");
			var expected = Hex("642cd3d60000c3500802e5d40007000000040000000f08c1e80a10021802280a3000609a080000002d0002348e004378e0e141bfe148436ed78c00000000032047a70000041a00");

			Assert.Equal(expected, BehaviorControlCenterCodec.Build(BehaviorAcks.CastInterruptAbort(beginArgs, cancel, 0x0802E5D4, 2)));
		}

		[Fact]
		public void EquipIntoSheathSlot_AckMatchesCapture() // frames 369 -> 375, Mode 5: oar into slot 0x14
		{
			var request = Hex(
				"00000078642cd3d60000c350021663dd0003000000090000000a089bc8021005280a3000000000000000001400005ace00005ace000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000");
			var expected = Hex(
				"642cd3d60000c350021663dd0004000000090000000c089bc80210051805280a3000000000000000001400005ace00005ace0000000000000001000000008b2978e3ae53830b0d5b7d662985a0fe8b2978e3ae53830b0d5b7d662985a0feb290805c29e627cab290805c29e627ca000000000000000000000000");

			Assert.Equal(expected, BehaviorControlCenterCodec.Build(BehaviorAcks.EquipMoveAck(request, Instance, 5, Oar)));
		}

		[Fact]
		public void EquipIntoSlot_AckMatchesCapture() // frames 285 -> 289, Mode 1: oar into main hand (slot 6)
		{
			var request = Hex(
				"00000078642cd3d60000c350021663dd0003000000090000000a089bc8021001280a3000000000000000000600005ace00005ace000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000");
			var expected = Hex(
				"642cd3d60000c350021663dd0004000000090000000c089bc80210011801280a3000000000000000000600005ace00005ace0000000000000001000000008b2978e3ae53830b0d5b7d662985a0fe8b2978e3ae53830b0d5b7d662985a0feb290805c29e627cab290805c29e627ca000000000000000000000000");

			Assert.Equal(expected, BehaviorControlCenterCodec.Build(BehaviorAcks.EquipMoveAck(request, Instance, 1, Oar)));
		}

		[Fact]
		public void UnarmedAirSwing2_AckMatchesCapture() // frames 501 -> 509, Mode 9 (non-zero charge float in the tail)
		{
			var request = Hex("00000040642cd3d60000c350021663dd0003000000110000000e08c0c7021009280a300058406040000000003e5f3b6500000001000000000000000000000000000bbc4b");
			var expected = Hex("642cd3d60000c350021663dd0004000000110000001008c0c70210091809280a300058406040000000003e5f3b6500000001000000000000000000000000000bbc4b000000000000000000000000");

			Assert.Equal(expected, BehaviorControlCenterCodec.Build(BehaviorAcks.EchoAck(request, Instance, 9)));
		}

		[Fact]
		public void UnarmedAirSwing_AckMatchesCapture() // frames 481 -> 486, Mode 8
		{
			var request = Hex("00000040642cd3d60000c350021663dd0003000000110000000e08c6d3061008280a300058326024000000000000000000000001000000000000000000000000000bbc46");
			var expected = Hex("642cd3d60000c350021663dd0004000000110000001008c6d30610081808280a300058326024000000000000000000000001000000000000000000000000000bbc46000000000000000000000000");

			Assert.Equal(expected, BehaviorControlCenterCodec.Build(BehaviorAcks.EchoAck(request, Instance, 8)));
		}

		[Fact]
		public void UnequipOutOfSlot_AckMatchesCapture() // frames 317 -> 320, Mode 2: out of main hand, null identity
		{
			var request = Hex(
				"00000078642cd3d60000c350021663dd0003000000090000000a089ac8021002280a3000000000060000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000");
			var expected = Hex(
				"642cd3d60000c350021663dd0004000000090000000c089ac80210021802280a300000000006000000000000000000000000000000000000000000000000b290805c29e627cab290805c29e627cab290805c29e627cab290805c29e627cab290805c29e627cab290805c29e627ca000000000000000000000000");

			Assert.Equal(expected, BehaviorControlCenterCodec.Build(BehaviorAcks.EquipMoveAck(request, Instance, 2, null!)));
		}

		// The hit-result block is `[u32 targetCount][targets][8 x u32][u32 resultCount][41-byte results]` (#536),
		// decoded from the corpus after the client's reader showed the attack commands read a COUNTED record loop.
		// This pins the arithmetic the decode rests on - `40 + 8*targets + 41*results` - which accounts for ALL 3687
		// captured s2c attack tails, so a change to the block's layout has to face it. (The middle is 9 words rather
		// than 8 on 28 of them, selected by its own second word; we always send the 8-word form, whose selector is
		// 1.)
		//
		// ⚠️ The single-target form is the one production sends and it must stay byte-identical: the counts move
		// together in most captures (one extra target, one extra result), which makes the lengths look like a single
		// 49-byte stride. 49 is 8 + 41, not one record - modelling it as one is the trap this test exists to hold
		// shut.
		[Theory]
		[InlineData(0, 0, 40)] // the air swing - 130 captured tails carry no targets and no results
		[InlineData(0, 1, 81)] // the ordinary single hit - what HitResultAck sends
		[InlineData(1, 2, 130)]
		[InlineData(2, 3, 179)]
		public void HitResultBlock_LengthIsHeadPlusTargetsPlusResults(int targets, int results, int expected)
		{
			var block = BehaviorAcks.HitResultBlock(
				[.. Enumerable.Range(1, targets).Select(i => (uint)(0x1000 + i))],
				[.. Enumerable.Range(1, results).Select(i => new BehaviorAcks.HitResult((uint)(0x2000 + i), (uint)(10 * i)))]);

			Assert.Equal(expected, block.Length);
			Assert.Equal((uint)targets, block.ReadU32Be(0));
			Assert.Equal((uint)results, block.ReadU32Be(4 + 8 * targets + 32));
		}

		// The convenience form production calls still builds exactly the single-target block - the decode was a
		// re-expression, not a wire change.
		[Fact]
		public void HitResultBlock_SingleTargetForm_IsUnchanged()
		{
			Assert.Equal(
				BehaviorAcks.HitResultBlock([], [new BehaviorAcks.HitResult(0x2001, 77)]),
				BehaviorAcks.HitResultBlock(0x2001, 77));
		}

		private static byte[] Hex(string s)
		{
			return Convert.FromHexString(s);
		}
	}
}
