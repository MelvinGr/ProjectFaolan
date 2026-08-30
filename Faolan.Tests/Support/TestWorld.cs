using Faolan.Core.Database.Repositories;
using Faolan.GameServer.Quest;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Faolan.Tests.Support
{
	// Builds a WorldBroadcaster for the tests that exercise its broadcast paths directly.
	//
	// It exists because the broadcaster takes two collaborators that CANNOT be `null!` the way its database /
	// intro-service / combos arguments can: it calls into QuestBroadcaster on every streamed-in NPC, and that call
	// happens before the `Character == null` guard the tests currently rely on. Both are cheap and pure, so the
	// helper wires the real ones rather than a fake - a test that streams an NPC in then exercises the same marker
	// path production does. The handle allocator and the ambient pools are shared with NpcBroadcaster in production
	// and so are constructed here too; nothing in these tests spawns a runtime entity, but the ctor requires them.
	public static class TestWorld
	{
		public static WorldBroadcaster Broadcaster(WorldSimulation sim, IDatabaseRepository? database = null, ILogger? logger = null)
		{
			var log = logger ?? NullLogger.Instance;
			var markers = new QuestMarkerResolver(database!, log);
			var handles = new RuntimeHandleAllocator();

			return new WorldBroadcaster(sim, database!, log, null!, null!, markers, new QuestBroadcaster(sim, database!, markers, log), handles,
				new AmbientPoolService(sim, database!, handles, null!, log));
		}
	}
}
