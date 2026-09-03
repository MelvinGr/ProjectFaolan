using Faolan.Core.Database.Repositories;
using Faolan.Core.Network;
using Microsoft.Extensions.DependencyInjection;

namespace Faolan.CSPlayerAgent
{
	// The CS tier's services, CONTAINER-registered the way the GameServer's are (`AddRealmGameServer`): the listener
	// takes them by constructor injection instead of `new`-ing them in its own ctor with a `ClientForCharacter`
	// delegate closed over `this`. Listed in dependency order - documentation only, MS.DI resolves by type.
	//
	// ⚠️ Every service here takes `ILogger<CsPlayerAgentListener>`, not a bare `ILogger`: when this process also
	// hosts a realm, `AddRealmGameServer` registers the bare `ILogger` as the GameServer's realm-tagged category, and
	// a CS service asking for the bare form would silently log under it. The typed form is exactly the logger the
	// listener used to hand them, so the categories in faolan.log do not move.
	//
	// `CsPlayerAgentCompositionTests` is the gate: a missing registration is not a build error, `ValidateOnBuild` is
	// what names it.
	public static class CsPlayerAgentServices
	{
		// The service key of the CS tier's `ConnectionRegistry`; consumers take `[FromKeyedServices(ConnectionsKey)]`.
		public const string ConnectionsKey = "CsPlayerAgent";

		public static IServiceCollection AddRealmCsPlayerAgent(this IServiceCollection services)
		{
			// The listener's own live connections, shared with every service that answers "send to this character's
			// CS client" - registered rather than owned by ServerBase so those services resolve it directly. KEYED,
			// because the GameServer tier registers a `ConnectionRegistry` of its own for the world connections and
			// the two must never be one set: a character is on both listeners at once, as two different clients.
			services.AddKeyedSingleton<ConnectionRegistry>(ConnectionsKey);

			// The repository SLICES the team and guild services take (#928 item 9); the shared singleton already is one, MS.DI just
			// does not infer that. The same forward AddRealmGameServer makes - registered here too so a CS-only test
			// composition resolves.
			services.AddSingleton<ICharacterRepository>(sp => sp.GetRequiredService<IDatabaseRepository>());
			services.AddSingleton<ICharacterInventoryRepository>(sp => sp.GetRequiredService<IDatabaseRepository>());
			services.AddSingleton<ICharacterQuestRepository>(sp => sp.GetRequiredService<IDatabaseRepository>());
			services.AddSingleton<ICharacterStatRepository>(sp => sp.GetRequiredService<IDatabaseRepository>());
			services.AddSingleton<ICharacterProgressionRepository>(sp => sp.GetRequiredService<IDatabaseRepository>());
			services.AddSingleton<IGuildRepository>(sp => sp.GetRequiredService<IDatabaseRepository>());

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
