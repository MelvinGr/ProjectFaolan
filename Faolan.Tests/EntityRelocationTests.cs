using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.GameServer.World;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// #917: relocating a LIVE non-player entity - the primitive `/teleporttarget` needed to accept an NPC.
	//
	// The ticket asked whether a large jump needs a despawn/respawn pair rather than a move. It needs BOTH, and which
	// one a client gets is a property of the VIEWER: a viewer that keeps the entity in sight is snapped, one that
	// loses it is despawned, and a player standing near the destination who never saw it is spawned. The last two are
	// what an interest pass produces, so `RelocateEntity` snaps and then re-runs the #778 sweep for the zone - which
	// is what these gate.
	public class EntityRelocationTests
	{
		private const uint MapId = 4010; // Tortage Beach
		private const uint PlayerHandle = 0x01316e68;
		private const uint SecondPlayerHandle = 0x01316e69;
		private const uint MobHandle = 0x01400001;

		[Fact]
		public async Task RelocateEntity_MovesTheModelAndSnapsTheViewers()
		{
			var (sim, output, mob) = Harness();

			var destination = new Vector3(500f, 12f, -300f);
			await sim.RelocateEntity(mob, destination);

			Assert.Equal(destination, mob.Position);

			// MoveFrom travels with Position: it is the FROM point of the segment the tick's broadcaster sends, so
			// leaving it behind would render the next ordinary step as a walk all the way back across the map.
			Assert.Equal(destination, mob.MoveFrom);

			var snap = Assert.Single(output.Snaps);
			Assert.Equal(MobHandle, snap.Handle);
			Assert.Equal(destination, snap.Position);
		}

		[Fact]
		// Every player in the ZONE, not just the two interest radii - the union of "was in range of the origin" and
		// "is in range of the destination" is what has to be re-resolved, and the pass sends nothing to a player whose
		// view did not change.
		public async Task RelocateEntity_ReResolvesInterestForEveryPlayerInTheZone()
		{
			var (sim, output, mob) = Harness(secondPlayerAt: new Vector3(5000f, 0f, 5000f));

			var resolved = await sim.RelocateEntity(mob, new Vector3(500f, 12f, -300f));

			Assert.Equal(2, resolved);
			Assert.Equal(2, output.InterestResolves.Count);
		}

		[Fact]
		// A relocation is LIVE ONLY: it does not touch the placement the entity respawns at, so its spawn position is
		// untouched. Moving a stuck mob must not silently re-place it.
		public async Task RelocateEntity_LeavesTheSpawnPositionAlone()
		{
			var (sim, _, mob) = Harness();
			var placed = mob.SpawnPosition;

			await sim.RelocateEntity(mob, new Vector3(500f, 12f, -300f));

			Assert.Equal(placed, mob.SpawnPosition);
			Assert.NotEqual(mob.Position, mob.SpawnPosition);
		}

		private static (WorldSimulation Sim, RecordingWorldOutbound Output, WorldEntity Mob) Harness(Vector3? secondPlayerAt = null)
		{
			var output = new RecordingWorldOutbound();
			var parts = TestWorld.Build();
			var sim = parts.Sim;
			sim.BindOutbound(output);

			var client = new RecordingNetworkClient(PlayerHandle);
			var player = new WorldEntity(PlayerHandle, MapId, true, "Watcher", client);
			sim.Players[client] = player;
			sim.World.Place(player, MapId, Vector3.Zero, Quaternion.Identity);

			if (secondPlayerAt is { } far)
			{
				var otherClient = new RecordingNetworkClient(SecondPlayerHandle);
				var other = new WorldEntity(SecondPlayerHandle, MapId, true, "Bystander", otherClient);
				sim.Players[otherClient] = other;
				sim.World.Place(other, MapId, far, Quaternion.Identity);
			}

			var mob = new WorldEntity(MobHandle, MapId, false, "Stuck Pict", null!);
			sim.World.Place(mob, MapId, Vector3.Zero, Quaternion.Identity);

			return (sim, output, mob);
		}
	}
}
