using Faolan.Core.Network;
using Microsoft.Extensions.DependencyInjection;

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

		public static IServiceCollection AddRealmCsPlayerAgent(this IServiceCollection services)
		{
			// The listener's own live connections, shared with every service that answers "send to this character's CS
			// client" - registered rather than owned by ServerBase so those services resolve it directly. Keyed,
			// because the GameServer tier registers a `ConnectionRegistry` of its own for the world connections and the
			// two must never be one set: a character is on both listeners at once, as two different clients.
			services.AddKeyedSingleton<ConnectionRegistry>(ConnectionsKey);

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
