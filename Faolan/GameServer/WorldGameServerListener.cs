using Faolan.Core.Database;
using Faolan.Core.Network.AgentServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	// A SECOND GameServer listener, bound to a different port from the creator listener. Retail runs the
	// GameServer on two ports (creator + world); confirmed in every create capture: e.g. creator :7027/world
	// :7031 (stricken998) and creator :7018/world :7012 (hoxxling). After character creation the creator sends
	// a 0x2002 enter-world handoff advertising the world port, and the client opens a brand-new connection
	// there (HandleDisconnect redir=1) for the world/intro phase.
	//
	// All behaviour is inherited from GameServerListener - the only difference is the port. A distinct subclass
	// is required so AddScopedHostedService<T> registers two non-colliding DI services (two factories under the
	// same T would override each other). The PendingCreateWorldEntry set that bridges the editor connection
	// (creator port) and the world connection (this port) is static on the base class, so it is shared across
	// both listener instances.
	public sealed class WorldGameServerListener : GameServerListener
	{
		public WorldGameServerListener(ILogger logger, IConfiguration configuration, IDatabaseRepository database, ushort port, IAgentServerDispatcher agentServerDispatcher) : base(logger, configuration, database, port,
			agentServerDispatcher)
		{
		}
	}
}
