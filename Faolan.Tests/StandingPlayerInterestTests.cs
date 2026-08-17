using System.Numerics;
using System.Threading.Tasks;
using Faolan.GameServer.World;
using Faolan.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// #778: the interest pass nobody drove - the one for a player STANDING STILL while the world walks past them.
	//
	// Every other pass is triggered by the player: `BroadcastPlayerMove`, `BroadcastPlayerEntry`,
	// `RefreshQuestVisibility`. Nothing was triggered by the SUBJECT moving, and `BroadcastNpcMove` only sends to
	// viewers who already hold the handle - so an NPC that walked out of range froze on the standing player's screen
	// for ever, and one that walked in was invisible until they next moved. It cannot hang off the NPC's own move
	// effect either: a route walker emits none (#716 issues the beat to the client and walks it there), so the
	// entities most in need of it are exactly the ones with no move to hook.
	public class StandingPlayerInterestTests
	{
		private const uint MapId = 2000; // Old Tarantia - where the walked beats are
		private const uint PlayerHandle = 0x01316e68;
		private const uint NpcHandle = 0x01400001;

		// The zone the harness placed everything in, for the gate that reads the resolve rather than the packets.
		private Zone? _zone;

		// A walker that left the interest range is despawned, with the player never having moved.
		[Fact]
		public async Task AnNpcThatWalkedOutOfRange_IsDespawnedForAStandingPlayer()
		{
			var (broadcaster, client, player, npc) = Harness();

			// The player already sees it - Known AND Confirmed, since #245 only despawns what the client built.
			player.Known.Add(NpcHandle);
			player.Confirmed.Add(NpcHandle);

			npc.Position = new Vector3(WorldSimulation.PlayerInterestRange + 50f, 0, 0); // walked its beat away

			await broadcaster.ResolveInterest(client);

			Assert.Single(client.Sent);
			Assert.DoesNotContain(NpcHandle, player.Known);
		}

		// ...and one still inside it is left alone: an idle sweep must not churn what is already rendered.
		[Fact]
		public async Task AnNpcStillInRange_ProducesNoTraffic()
		{
			var (broadcaster, client, player, npc) = Harness();
			player.Known.Add(NpcHandle);
			player.Confirmed.Add(NpcHandle);

			npc.Position = new Vector3(40f, 0, 0);

			await broadcaster.ResolveInterest(client);

			Assert.Empty(client.Sent);
			Assert.Contains(NpcHandle, player.Known);
		}

		// The other direction, and the one a standing player could NEVER get before: a walker that arrived from
		// outside the range produces an ARRIVAL. Asserted on the resolve rather than on the dispatched packet - the
		// spawn send reads the quest-giver map, and what is new here is the pass running at all, not how a spawn is
		// framed (`DispatchInterest` is shared verbatim with the player-move path).
		[Fact]
		public void AnNpcThatWalkedIntoRange_ResolvesAsAnArrivalForAStandingPlayer()
		{
			var (_, _, player, npc) = Harness();
			npc.Position = new Vector3(40f, 0, 0); // arrived on its beat

			var events = InterestManager.ResolveMove(_zone!, player, WorldSimulation.PlayerInterestRange, true);

			var arrival = Assert.Single(events);
			Assert.Equal(InterestEventType.Spawn, arrival.Type);
			Assert.Equal(NpcHandle, arrival.Subject.Handle);
			Assert.Equal(player.Handle, arrival.Recipient.Handle);
		}

		// #245's gate still holds on this path: a handle the client never confirmed building is dropped SILENTLY.
		// Sending an n3ToClientQuit for one crashes a still-loading client on the unknown handle.
		[Fact]
		public async Task AnUnconfirmedHandle_IsForgottenWithoutADespawnPacket()
		{
			var (broadcaster, client, player, npc) = Harness();
			player.Known.Add(NpcHandle); // spawned, never polled

			npc.Position = new Vector3(WorldSimulation.PlayerInterestRange + 50f, 0, 0);

			await broadcaster.ResolveInterest(client);

			Assert.Empty(client.Sent);
			Assert.DoesNotContain(NpcHandle, player.Known);
		}

		// ⛔ ...and it does NOT run for a player whose world entry has not yet DELIVERED its packets. World entry places
		// the player in the zone (which is what makes them visible to this sweep) seconds before it flushes the
		// MapChange/snapshot/NPC burst, so without this gate the tick got there first: it streamed every in-range NPC
		// to a client that had not been told which playfield it was in, stamped with the zone's minted fallback token
		// rather than the map's. The client drops those, and because the sweep SEEDS them into Known the entry burst
		// then finds nothing in range ("0 in-range NPCs") and never corrects it - every NPC in the entry radius stays
		// invisible for the whole session while remaining alive server-side, taking and dealing damage.
		[Fact]
		public async Task AnEnteringPlayer_IsNotStreamedToBeforeTheirEntryBurstIsDelivered()
		{
			var (broadcaster, client, player, npc) = Harness();
			client.Session.WorldEntryDelivered = false; // placed in the zone, entry packets not flushed yet
			npc.Position = new Vector3(40f, 0, 0); // well inside the interest range

			await broadcaster.ResolveInterest(client);

			Assert.Empty(client.Sent);
			// and crucially NOT seeded: a seeded handle is one the entry burst will skip as already known.
			Assert.DoesNotContain(NpcHandle, player.Known);
		}

		// ⚠️ The other half of #778: BOUNDARY CHURN. A flat radius has no memory, so a walker whose beat happens to
		// run along the interest edge crosses it twice per lap and is spawned and despawned every time - a whole
		// FA015807 spawn record each way, for an NPC that never actually goes anywhere.
		//
		// This paces one NPC back and forth across the line and counts the RESOLVE, not the packets: the despawn half
		// is gated on `Confirmed`, which only a client poll sets, so counting sends would score a churning pass as
		// quiet for the wrong reason (the second despawn is suppressed, the spawns are not). The events are the
		// churn; what reaches the wire depends on what the client has confirmed.
		[Fact]
		public void AWalkerPacingTheBoundary_IsNotRespawnedEveryLap()
		{
			var (_, _, player, npc) = Harness();
			player.Known.Add(NpcHandle);
			player.Confirmed.Add(NpcHandle);

			var events = 0;
			// Six laps of a small dither straddling the range - the shape of a beat that runs along the edge.
			for (var lap = 0; lap < 6; lap++)
			{
				foreach (var offset in (float[])[-1f, 1f, 2f, 1f])
				{
					npc.Position = new Vector3(WorldSimulation.PlayerInterestRange + offset, 0, 0);
					events += InterestManager.ResolveMove(_zone!, player, WorldSimulation.PlayerInterestRange, true).Count;
				}
			}

			// The NPC is never more than 3 units from the line, so it should be resolved at most once. Without
			// hysteresis this is one despawn and one spawn per lap.
			Assert.True(events <= 1, $"a walker dithering across the interest edge resolved {events} interest events; it never moved more than 3 units");
		}

		// ⛔ ...and the band is a DESPAWN-side rule only. An entity inside it that the player does NOT already hold
		// must not be introduced - otherwise the effective interest range is simply 165 u and the churn has moved
		// outward rather than gone away.
		[Fact]
		public void AnUnknownNpcInsideTheHysteresisBand_IsNotSpawned()
		{
			var (_, _, player, npc) = Harness();
			npc.Position = new Vector3(WorldSimulation.PlayerInterestRange + 5f, 0, 0); // in the band, never seen

			var events = InterestManager.ResolveMove(_zone!, player, WorldSimulation.PlayerInterestRange, true);

			Assert.Empty(events);
			Assert.DoesNotContain(NpcHandle, player.Known);
		}

		// ...while one the player DOES hold is kept there, which is the whole point of the band.
		[Fact]
		public void AKnownNpcInsideTheHysteresisBand_IsKept()
		{
			var (_, _, player, npc) = Harness();
			player.Known.Add(NpcHandle);
			player.Confirmed.Add(NpcHandle);
			npc.Position = new Vector3(WorldSimulation.PlayerInterestRange + 5f, 0, 0);

			var events = InterestManager.ResolveMove(_zone!, player, WorldSimulation.PlayerInterestRange, true);

			Assert.Empty(events);
			Assert.Contains(NpcHandle, player.Known);
		}

		// ...and past the band it goes, so a genuine departure is not held for ever.
		[Fact]
		public void AKnownNpcBeyondTheHysteresisBand_IsDespawned()
		{
			var (_, _, player, npc) = Harness();
			player.Known.Add(NpcHandle);
			player.Confirmed.Add(NpcHandle);
			npc.Position = new Vector3(WorldSimulation.PlayerInterestRange + InterestManager.DespawnHysteresis + 1f, 0, 0);

			var despawn = Assert.Single(InterestManager.ResolveMove(_zone!, player, WorldSimulation.PlayerInterestRange, true));

			Assert.Equal(InterestEventType.Despawn, despawn.Type);
			Assert.DoesNotContain(NpcHandle, player.Known);
		}

		private (WorldBroadcaster Broadcaster, RecordingNetworkClient Client, WorldEntity Player, WorldEntity Npc) Harness()
		{
			var sim = new WorldSimulation(null!, null!, null!, NullLogger.Instance);
			var broadcaster = new WorldBroadcaster(sim, null!, NullLogger.Instance, null!, null!);

			var client = new RecordingNetworkClient(PlayerHandle);
			// The sweep only runs for a client whose world entry has FLUSHED its packets; a player standing still is
			// past that by definition. The window where it is false has its own gate below.
			client.Session.WorldEntryDelivered = true;
			var player = new WorldEntity(PlayerHandle, MapId, true, "Standing", client);
			sim.Players[client] = player;
			var zone = _zone = sim.World.Place(player, MapId, Vector3.Zero, Quaternion.Identity);
			zone.PlayfieldToken = WorldState.DefaultPlayfieldToken; // a despawn without one is skipped by design

			var npc = new WorldEntity(NpcHandle, MapId, false, "City Guard", null!);
			sim.World.Place(npc, MapId, Vector3.Zero, Quaternion.Identity);

			return (broadcaster, client, player, npc);
		}
	}
}
