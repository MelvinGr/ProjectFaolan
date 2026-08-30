using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.GameServer.World;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// #398: the in-range NPCs ride the world-ENTRY BURST, before the client's first move, which is the shape retail
	// sends (fr64-75 of the Hoxxling beach login capture). BuildEntryNpcBurst is what builds them.
	//
	// ⛔ It was DEAD for three weeks and said so in the log every single login - `0 in-range NPCs` on a map holding
	// 168 of them. The guard at the top read `entity?.SpawnBody == null`, and #429 then stopped a PLAYER from ever
	// having a SpawnBody at all (a player's record is rebuilt from live state per send, because a cached one describes
	// them as they were at login). #429 converted the two other places where SpawnBody "had quietly doubled as the
	// is-this-spawnable-yet flag" and missed this one, so every entry took the early return.
	//
	// Nothing here needs the player's own body - the records it builds are the NPCS'. These gates say that in the one
	// way that cannot rot: the player entity in them has NO SpawnBody, exactly as a live one never will.
	public class EntryNpcBurstTests : IDisposable
	{
		private const uint MapId = 4010;
		private const uint PlayerHandle = 0x01000001;
		private const uint NpcHandle = 0x01400001;

		private readonly List<string> _databases = [];

		public void Dispose()
		{
			foreach (var path in _databases)
				File.Delete(path);
		}

		[Fact]
		public async Task AnInRangeNpc_RidesTheEntryBurst_ForAPlayerWithNoSpawnBodyOfTheirOwn()
		{
			var (broadcaster, client, player, npc) = await Harness();
			Assert.Null(player.SpawnBody); // the state every live player is in since #429

			var (records, npcs) = await broadcaster.BuildEntryNpcBurst(client);

			Assert.Single(records);
			Assert.Same(npc, Assert.Single(npcs));
			// and it is SEEDED, which is what stops the interest layer re-spawning or culling it afterwards (#245).
			Assert.Contains(NpcHandle, player.Known);
			// the handle -> name/template routing the click path reads is registered at the same time
			Assert.Equal(npc.Name, client.Session.SpawnedNpcNamesByHandle[NpcHandle]);
		}

		// The complement, so the gate above cannot pass by simply never filtering: an NPC outside the interest range
		// is not in the burst, and is not seeded either.
		[Fact]
		public async Task AnOutOfRangeNpc_DoesNotRideTheEntryBurst()
		{
			var (broadcaster, client, player, npc) = await Harness();
			npc.Position = new Vector3(WorldSimulation.PlayerInterestRange + 50f, 0, 0);

			var (records, npcs) = await broadcaster.BuildEntryNpcBurst(client);

			Assert.Empty(records);
			Assert.Empty(npcs);
			Assert.DoesNotContain(NpcHandle, player.Known);
		}

		private async Task<(WorldBroadcaster Broadcaster, RecordingNetworkClient Client, WorldEntity Player, WorldEntity Npc)> Harness()
		{
			var (options, path) = TestDb.NewSeededDatabase(PlayerHandle);
			_databases.Add(path);

			var sim = TestWorld.Simulation();
			var broadcaster = TestWorld.Broadcaster(sim, new DatabaseRepository(new Factory(options)));

			var client = new RecordingNetworkClient(PlayerHandle);
			var player = new WorldEntity(PlayerHandle, MapId, true, "Entering", client);
			sim.Players[client] = player;
			var zone = sim.World.Place(player, MapId, Vector3.Zero, Quaternion.Identity);
			zone.PlayfieldToken = WorldState.DefaultPlayfieldToken;

			// A real spawn record, so the burst has something to frame - it is the NPC's body that matters here.
			var npc = new WorldEntity(NpcHandle, MapId, false, "City Guard", null!)
			{
				SpawnBody = Golden.Bytes("SpawnNpcCrocodile")[4..]
			};
			sim.World.Place(npc, MapId, new Vector3(10f, 0, 0), Quaternion.Identity);

			return (broadcaster, client, player, npc);
		}

		private sealed class Factory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
		{
			public DatabaseContext CreateDbContext() => new DatabaseContext(options);
			public Task<DatabaseContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(new DatabaseContext(options));
		}
	}
}
