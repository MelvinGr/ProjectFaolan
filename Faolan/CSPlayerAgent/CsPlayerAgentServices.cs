using Faolan.Core.Helpers;
using Faolan.Core.Network;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Faolan.CSPlayerAgent
{
	// The CS tier's services, container-registered the way the GameServer's are (`AddRealmGameServer`): the listener
	// takes them by constructor injection. Listed in dependency order, which is documentation only - MS.DI resolves by
	// type.
	//
	// Every service here takes `ILogger<CsPlayerAgentListener>`, never a bare `ILogger`: when this process also hosts a
	// realm, `AddRealmGameServer` registers the bare `ILogger` as the GameServer's realm-tagged category, and a CS
	// service asking for the bare form would silently log under it.
	//
	// `CsPlayerAgentCompositionTests` is the gate: a missing registration is not a build error, `ValidateOnBuild` is
	// what names it.
	public static class CsPlayerAgentServices
	{
		// The service key of the CS tier's `ConnectionRegistry`; consumers take `[FromKeyedServices(ConnectionsKey)]`.
		public const string ConnectionsKey = "CsPlayerAgent";

		// The service key of the CS tier's `WorkLoop`: the one owner of its state (teams, raids, guilds, matches), as each
		// zone owns its own. Consumers take `[FromKeyedServices(LoopKey)]`.
		public const string LoopKey = "CsPlayerAgentLoop";

		public static IServiceCollection AddRealmCsPlayerAgent(this IServiceCollection services)
		{
			// The listener's own live connections, shared with every service that answers "send to this character's CS
			// client" - registered rather than owned by ServerBase so those services resolve it directly. Keyed,
			// because the GameServer tier registers a `ConnectionRegistry` of its own for the world connections and the
			// two must never be one set: a character is on both listeners at once, as two different clients.
			services.AddKeyedSingleton<ConnectionRegistry>(ConnectionsKey);

			// Its packets, its disconnects, its timers and the world's calls into it all run here, one at a time. A zone
			// may wait on this loop; this loop never waits on a zone (its calls into one are queued), so the two can never
			// wait on each other.
			services.AddKeyedSingleton(LoopKey, (sp, _) => new WorkLoop("CS tier", sp.GetRequiredService<ILogger<CsPlayerAgentListener>>())
			{
				Owner = LoopKey
			});

			services.AddSingleton<GuildService>();
			services.AddSingleton<TeamService>();
			services.AddSingleton<RaidService>();
			services.AddSingleton<GroupFinderService>();
			services.AddSingleton<PvpMatchMakingService>();
			services.AddSingleton<PvpMatchService>();

			return services;
		}
	}
}
