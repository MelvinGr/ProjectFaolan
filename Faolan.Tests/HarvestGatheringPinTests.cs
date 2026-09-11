using System;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Constants;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// A harvest node's map pin is `WaypointType` (0xAC) on the dynel. Retail flips it to `ResourceGatheringActive`
	// beside the first swing of a bout and restores the node's own kind 10 s after the last swing. The bytes are the
	// captured push; the timing is the zone's countdown.
	public class HarvestGatheringPinTests
	{
		// demol80-demonologist_login_gameplay_annotated_1 frames 3407/3456, dynel 0x4D0 (a pyrite lode, kind GoldNode):
		// the gathering push and, ten seconds later, the restore.
		[Fact]
		public void TheGatheringPush_MatchesTheCapturedBytes()
		{
			Assert.Equal("f98e10b30000c879000004d000000000ac0000001d",
				Convert.ToHexStringLower(StatCodec.Build(StatIirs.ForWorldObject(EntityClass.Destructible, 0x4D0, CharacterStatId.WaypointType, (uint)WaypointType.ResourceGatheringActive))));
			Assert.Equal("f98e10b30000c879000004d000000000ac00000011",
				Convert.ToHexStringLower(StatCodec.Build(StatIirs.ForWorldObject(EntityClass.Destructible, 0x4D0, CharacterStatId.WaypointType, (uint)WaypointType.GoldNode))));
		}

		[Fact]
		public void TheWindow_StartsOnTheFirstSwing_RestartsOnEveryLaterOne_AndRunsOutOnceAfterTheLast()
		{
			var zone = new Zone(1);
			var node = new LiveWorldObject(0x4D0, new WorldObject { Name = "Pyrite Lode", WaypointType = (uint)WaypointType.GoldNode });

			Assert.True(zone.MarkGathering(node)); // the first swing puts the pin on the wire
			Assert.Empty(zone.TickGatheringPins(Zone.GatheringPinSeconds - 1));

			Assert.False(zone.MarkGathering(node)); // a swing inside the window sends nothing, but restarts the countdown
			Assert.Empty(zone.TickGatheringPins(Zone.GatheringPinSeconds - 1)); // would have expired without the restart

			var due = zone.TickGatheringPins(2).ToList();
			Assert.Equal([node], due);
			Assert.Empty(zone.TickGatheringPins(Zone.GatheringPinSeconds)); // and only once

			Assert.True(zone.MarkGathering(node)); // the next bout starts a new pin
		}
	}
}
