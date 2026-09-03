using System;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.GameServer.World;
using Faolan.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// A loot bag leaving the world is a WORLD event, and it used to be sent to one player (#698).
	//
	// Retail's captured completion is `DispatchCloseLootBag -> looter resets -> n3ToClientFade -> UpdateSimpleDynel`.
	// The first three are the LOOTER's - their window closes, their looter rights reset - but the last two are the
	// bag going away, and `AfterLootTake` sent all five to the looter alone. Every other player in range kept the bag
	// on screen, and a client that keeps the object keeps everything hanging off it, which is what a lingering
	// lootable sparkle is.
	//
	// The expiry path had the complementary half of the same bug: it broadcast the FADE and not the consumed visual.
	public class LootBagFadeBroadcastTests
	{
		// The two bodies as retail sends them (hoxxling gameplay, bag 0x000021fe). Spelled as HEX rather than built
		// through the codecs on purpose: the byte-exact goldens for these live in `WorldLootTakeTests` and `LootTests`,
		// and `docs/protocol/gen/messages.md` cites whichever test names a codec - so calling `Build` here would
		// quietly demote the page's citation from a capture golden to this behaviour test.
		private const string CapturedFade = "66180b200000c87a000021fe";
		private const string CapturedConsumedVisual = "305364110000c87a000021fe000000000000000001000101";

		private const uint MapId = 4010; // Tortage Beach
		private const uint BagHandle = 0x000021FE; // the hoxxling capture's bag

		// Both halves of the sequence reach EVERY player in range, in the captured order.
		[Fact]
		public async Task Fade_ReachesEveryViewerInRange_WithTheConsumedVisual()
		{
			var sim = TestWorld.Simulation();
			var broadcaster = TestWorld.Broadcaster(sim);

			var position = new Vector3(100f, 0f, 100f);
			var looter = Place(sim, 0x01317CF8, position);
			var bystander = Place(sim, 0x013184AE, position + new Vector3(5f, 0f, 0f));
			var farAway = Place(sim, 0x0131830B, position + new Vector3(10_000f, 0f, 0f));

			var bag = new WorldEntity(BagHandle, MapId, false, "Bag of loot", null!); // a loot bag has no client, as ForLoot says
			sim.World.Place(bag, MapId, position, Quaternion.Identity);

			await broadcaster.BroadcastEntityFade(bag);

			foreach (var viewer in new[] { looter, bystander })
			{
				Assert.Equal(2, viewer.Sent.Count);
				Assert.Contains(CapturedFade, viewer.SentHex());
				Assert.Contains(CapturedConsumedVisual, viewer.SentHex());
			}

			Assert.Empty(farAway.Sent); // interest still bounds it - this is a broadcast, not a shout
		}

		// The consumed visual is the SECOND message, not the first: retail's order is fade then update, and a client
		// told the bag is spent before it is told to fade it is a different sequence.
		[Fact]
		public async Task TheConsumedVisual_FollowsTheFade()
		{
			var sim = TestWorld.Simulation();
			var broadcaster = TestWorld.Broadcaster(sim);

			var position = new Vector3(0f, 0f, 0f);
			var viewer = Place(sim, 0x01317CF8, position);
			var bag = new WorldEntity(BagHandle, MapId, false, "Bag of loot", null!); // a loot bag has no client, as ForLoot says
			sim.World.Place(bag, MapId, position, Quaternion.Identity);

			await broadcaster.BroadcastEntityFade(bag);

			Assert.Contains(CapturedFade, Convert.ToHexStringLower(viewer.Sent[0]));
			Assert.Contains(CapturedConsumedVisual, Convert.ToHexStringLower(viewer.Sent[1]));
		}

		private static RecordingNetworkClient Place(WorldSimulation sim, uint handle, Vector3 position)
		{
			var client = new RecordingNetworkClient(handle);
			var entity = new WorldEntity(handle, MapId, true, $"P{handle:x8}", client);
			sim.Players[client] = entity;
			sim.World.Place(entity, MapId, position, Quaternion.Identity);

			return client;
		}
	}
}
