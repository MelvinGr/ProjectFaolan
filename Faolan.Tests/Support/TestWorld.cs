using Faolan.Core.Database.Repositories;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.GameServer.Combat;
using Faolan.GameServer.Quest;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Faolan.Tests.Support
{
	// Builds a WorldBroadcaster for the tests that exercise its broadcast paths directly.
	//
	// It exists because the broadcaster takes two collaborators that cannot be `null!` the way its database /
	// intro-service / combos arguments can: it calls into QuestBroadcaster on every streamed-in NPC, and that call
	// happens before the `Character == null` guard the tests currently rely on. Both are cheap and pure, so the
	// helper wires the real ones rather than a fake - a test that streams an NPC in then exercises the same marker
	// path production does. The handle allocator and the ambient pools are shared with NpcBroadcaster in production
	// and so are constructed here too; nothing in these tests spawns a runtime entity, but the ctor requires them.
	public static class TestWorld
	{
		// A WorldSimulation with the world model, the player registry and the death service it now takes by
		// injection already built. The tests that construct it directly care about the tick and the world
		// model, not about which of those three a given field came from, so the helper keeps that off their
		// arrange blocks - and, like Broadcaster below, wires the real collaborators rather than a null.
		public static WorldSimulation Simulation(IDatabaseRepository? database = null, IChatServerDispatcher? agents = null,
			SpellAreaCatalog? spellAreas = null, ILogger? logger = null, TerrainService? terrain = null)
		{
			return Build(database, agents, spellAreas, logger, terrain).Sim;
		}

		// The same, when the test drives the death service as well - it is a sibling of the simulation now, not a
		// member of it, so a test that exercises both needs both handed back.
		public static WorldParts Build(IDatabaseRepository? database = null, IChatServerDispatcher? agents = null,
			SpellAreaCatalog? spellAreas = null, ILogger? logger = null, TerrainService? terrain = null)
		{
			var log = logger ?? NullLogger.Instance;
			var world = new WorldState(terrain);
			var players = new PlayerRegistry();
			var deaths = new PlayerDeathService(world, players, database!, database!, log);
			var buffTicks = new BuffTickService(world, players, database!, spellAreas!, log);

			return new WorldParts(new WorldSimulation(database!, database!, agents!, log, world, players, deaths, buffTicks, new NpcCombatService(buffTicks, log), terrain),
				deaths, buffTicks, world, players);
		}

		public sealed record WorldParts(WorldSimulation Sim, PlayerDeathService Deaths, BuffTickService BuffTicks, WorldState World, PlayerRegistry Players);

		public static WorldBroadcaster Broadcaster(WorldSimulation sim, IDatabaseRepository? database = null, ILogger? logger = null)
		{
			var log = logger ?? NullLogger.Instance;
			var markers = new QuestMarkerResolver(database!, database!, database!, log);
			var handles = new RuntimeHandleAllocator();

			return new WorldBroadcaster(sim, database!, log, null!, null!, markers, new QuestBroadcaster(sim, database!, database!, markers, log), handles,
				new AmbientPoolService(sim, database!, handles, null!, log));
		}
	}
}
