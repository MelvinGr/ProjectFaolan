using System;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Golden tests for the BehaviorControlCenter echo-acks, byte-exact against the armed+unarmed combat capture
	// (Captures/Stihetfv_soldier-conqueror_login_combat_armed-and-unarmed.pcapng, 2026-06-12). Each case is a real
	// client request (UP) paired with retail's reply (DOWN); request/reply hex includes the leading u32 body length
	// (the builders take the length-prefixed request and return the reply content without its length prefix).
	public class BehaviorAckTests
	{
		private const uint Instance = 0x021663DD; // the captured character

		// The captured Broken Oar (item 23246) identity quad, as carried by the equip ack (= Items.Data1..4).
		private static readonly Item Oar = new()
		{
			Id = 23246,
			Data1 = 0x8B2978E3,
			Data2 = 0xAE53830B,
			Data3 = 0x0D5B7D66,
			Data4 = 0x2985A0FE
		};

		[Fact]
		public void EquipIntoSheathSlot_AckMatchesCapture() // frames 369 -> 375, Mode 5: oar into slot 0x14
		{
			var request = Hex(
				"00000078642cd3d60000c350021663dd0003000000090000000a089bc8021005280a3000000000000000001400005ace00005ace000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000");
			var expected = Hex(
				"642cd3d60000c350021663dd0004000000090000000c089bc80210051805280a3000000000000000001400005ace00005ace0000000000000001000000008b2978e3ae53830b0d5b7d662985a0fe8b2978e3ae53830b0d5b7d662985a0feb290805c29e627cab290805c29e627ca000000000000000000000000");

			Assert.Equal(expected, BehaviorAckBody.BuildEquipMoveAckBody(request, Instance, 5, Oar));
		}

		[Fact]
		public void EquipIntoSlot_AckMatchesCapture() // frames 285 -> 289, Mode 1: oar into main hand (slot 6)
		{
			var request = Hex(
				"00000078642cd3d60000c350021663dd0003000000090000000a089bc8021001280a3000000000000000000600005ace00005ace000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000");
			var expected = Hex(
				"642cd3d60000c350021663dd0004000000090000000c089bc80210011801280a3000000000000000000600005ace00005ace0000000000000001000000008b2978e3ae53830b0d5b7d662985a0fe8b2978e3ae53830b0d5b7d662985a0feb290805c29e627cab290805c29e627ca000000000000000000000000");

			Assert.Equal(expected, BehaviorAckBody.BuildEquipMoveAckBody(request, Instance, 1, Oar));
		}

		[Fact]
		public void UnarmedAirSwing_AckMatchesCapture() // frames 481 -> 486, Mode 8
		{
			var request = Hex("00000040642cd3d60000c350021663dd0003000000110000000e08c6d3061008280a300058326024000000000000000000000001000000000000000000000000000bbc46");
			var expected = Hex("642cd3d60000c350021663dd0004000000110000001008c6d30610081808280a300058326024000000000000000000000001000000000000000000000000000bbc46000000000000000000000000");

			Assert.Equal(expected, BehaviorAckBody.BuildBehaviorEchoAckBody(request, Instance, 8));
		}

		[Fact]
		public void UnarmedAirSwing2_AckMatchesCapture() // frames 501 -> 509, Mode 9 (non-zero charge float in the tail)
		{
			var request = Hex("00000040642cd3d60000c350021663dd0003000000110000000e08c0c7021009280a300058406040000000003e5f3b6500000001000000000000000000000000000bbc4b");
			var expected = Hex("642cd3d60000c350021663dd0004000000110000001008c0c70210091809280a300058406040000000003e5f3b6500000001000000000000000000000000000bbc4b000000000000000000000000");

			Assert.Equal(expected, BehaviorAckBody.BuildBehaviorEchoAckBody(request, Instance, 9));
		}

		[Fact]
		public void UnequipOutOfSlot_AckMatchesCapture() // frames 317 -> 320, Mode 2: out of main hand, null identity
		{
			var request = Hex(
				"00000078642cd3d60000c350021663dd0003000000090000000a089ac8021002280a3000000000060000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000");
			var expected = Hex(
				"642cd3d60000c350021663dd0004000000090000000c089ac80210021802280a300000000006000000000000000000000000000000000000000000000000b290805c29e627cab290805c29e627cab290805c29e627cab290805c29e627cab290805c29e627cab290805c29e627ca000000000000000000000000");

			Assert.Equal(expected, BehaviorAckBody.BuildEquipMoveAckBody(request, Instance, 2, null));
		}

		private static byte[] Hex(string s)
		{
			return Convert.FromHexString(s);
		}
	}
}
