using Faolan.Core;
using Faolan.Core.Cli;
using Faolan.Core.Constants;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Network.Server.CsPlayerAgent;
using Faolan.GameServer.Apprentice;
using Faolan.GameServer.Combat;
using Faolan.GameServer.CreateFlow;
using Faolan.GameServer.Dialogue;
using Faolan.GameServer.Economy;
using Faolan.GameServer.Feats;
using Faolan.GameServer.Gm;
using Faolan.GameServer.Inventory;
using Faolan.GameServer.Progression;
using Faolan.GameServer.Quest;
using Faolan.GameServer.Scripting;
using Faolan.GameServer.Session;
using Faolan.GameServer.World;
using Faolan.GameServer.Wire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	// The realm's world services, registered in the container instead of hand-`new`ed in GameServerListener's ctor
	// (#826). Sixty-one constructions became sixty-one registrations, and the fourteen the listener only held in
	// order to build something else stopped being fields on it at all.
	//
	// ⚠️ THE ORDER OF THESE LINES IS DOCUMENTATION ONLY. MS.DI resolves by type, so nothing here depends on the
	// sequence - but the sequence is kept in dependency order because it is the only place the LAYERING of this
	// graph is legible (see the `construction_graph_layers` note: that ordering has settled several design
	// questions outright). Add a registration where its dependencies already are, not at the bottom.
	//
	// ⚠️ A factory lambda appears here for exactly two reasons, and both are stated at each site: a constructor
	// argument that is a VALUE off IConfiguration, or a single-op delegate seam (CLAUDE.md's `Func<>` shape). Two
	// factories additionally run a BIND - the post-construction wiring that resolves a genuine cycle - at the moment
	// the service that answers it comes into being, which is the earliest correct point and keeps it out of the
	// listener.
	public static class GameServerRealmServices
	{
		public static IServiceCollection AddRealmGameServer(this IServiceCollection services, HostRealm realm)
		{
			// ⚠️ The BARE `ILogger` every service below takes, realm-tagged so a multi-realm deployment can tell two
			// realms' lines apart in one log. It is deliberately not `ILogger<T>` (see the ⛔ non-goal in CLAUDE.md:
			// per-service categories would drop the realm tag and churn the file-logger filters).
			//
			// This registration is what the Tier-1 note rejected doing GLOBALLY, and the objection stands: any OTHER
			// container-resolved type asking for a non-generic ILogger would silently inherit this realm category.
			// Two things make it safe here rather than merely convenient - it is registered only when this process
			// hosts a realm, and `ListenerLoggerConventionTests` fails if any listener starts taking a bare ILogger.
			services.AddSingleton<ILogger>(sp => sp.GetRequiredService<ILoggerFactory>()
				.CreateLogger($"{typeof(GameServerListener).FullName}-{realm.Id}"));

			// The listener's own live connections, shared with the wire collaborators that answer "send to this
			// character" (EconomyOutbound, TeleportService, QuestShareOutbound, ApprenticeOutbound). It is registered
			// rather than owned by ServerBase precisely so those four can be resolved without reaching into it.
			services.AddSingleton<ConnectionRegistry>();
			services.AddSingleton<TagService>();

			// The narrower slices of IDatabaseRepository that individual services ask for, and the READ half of the
			// CS dispatcher. Both are interfaces the shared singleton already satisfies; MS.DI does not infer that,
			// so each is forwarded explicitly rather than the services being widened to take the whole repository.
			services.AddSingleton<ICharacterRepository>(sp => sp.GetRequiredService<IDatabaseRepository>());
			services.AddSingleton<ICatalogRepository>(sp => sp.GetRequiredService<IDatabaseRepository>());
			services.AddSingleton<INpcRepository>(sp => sp.GetRequiredService<IDatabaseRepository>());
			services.AddSingleton<ICsPlayerAgentProvider>(sp => sp.GetRequiredService<ICsPlayerAgentDispatcher>());

			// --- the world model, and the leaves it reads -------------------------------------------------------
			// TerrainService takes a configured DIRECTORY, so it is a factory rather than a plain registration.
			services.AddSingleton(sp => new TerrainService(sp.GetRequiredService<IConfiguration>().ContentTerrainDir, sp.GetRequiredService<ILogger>()));
			services.AddSingleton<SpellAreaCatalog>();
			// The world MODEL and the player registry are registered in their own right (#900) because two
			// collaborators share them: WorldSimulation, and PlayerDeathService - which must not hold the simulation,
			// so what it is handed is the state, not a reference back to its owner.
			services.AddSingleton(sp => new WorldState(sp.GetService<TerrainService>()));
			services.AddSingleton<PlayerRegistry>();
			services.AddSingleton<PlayerDeathService>();
			services.AddSingleton<BuffTickService>();
			services.AddSingleton<NpcCombatService>();
			services.AddSingleton<WorldSimulation>();
			services.AddSingleton<PlayerPersistenceService>();
			services.AddSingleton<TortageIntroService>();
			services.AddSingleton<ComboService>();

			// --- the world's wire, split by the question each answers (#826) -------------------------------------
			services.AddSingleton<QuestMarkerResolver>();
			services.AddSingleton<QuestBroadcaster>();
			services.AddSingleton<RuntimeHandleAllocator>();
			services.AddSingleton<AmbientPoolService>();
			services.AddSingleton<WorldBroadcaster>();
			services.AddSingleton<NpcBroadcaster>();
			services.AddSingleton<LootBroadcaster>();
			services.AddSingleton<QuestOutbound>();
			services.AddSingleton<IQuestOutbound>(sp => sp.GetRequiredService<QuestOutbound>());
			services.AddSingleton<WorldObjectService>();
			services.AddSingleton<TeleportService>();

			// The GM-bendable game clock every GameTime send reads (world entry's and the `.time` replies).
			services.AddSingleton<RealmClock>();

			// World entry - the ready bursts, the snapshot, the spawn poll. It was the listener's own until the
			// world-entry move; now the create flow below simply takes it, and the graph has no bound seam into the
			// listener left.
			services.AddSingleton<WorldEntryService>();

			// The create flow needs one op it does not own: the enter-world HANDOFF is TeleportService's, as a
			// single-op seam here.
			services.AddSingleton<CharacterCreationService>();
			services.AddSingleton(sp => new CreateFlowService(sp.GetRequiredService<ILogger>(), sp.GetRequiredService<IDatabaseRepository>(),
				sp.GetRequiredService<WorldSimulation>(), sp.GetRequiredService<WorldEntryService>(), sp.GetRequiredService<CharacterCreationService>(), async client =>
				{
					var teleport = sp.GetRequiredService<TeleportService>();
					await teleport.SendChangeMapPrep(client, Map.TortageBeachId, Map.NewPlayerSpawnSpawnPoint.Position, 0x32);
					await teleport.SendWorldHandoff(client);
				}));

			// --- progression + economy ------------------------------------------------------------------------
			services.AddSingleton<LevelUpOutbound>();
			services.AddSingleton<ILevelUpOutbound>(sp => sp.GetRequiredService<LevelUpOutbound>());
			services.AddSingleton<LevelUpService>();
			services.AddSingleton<EconomyOutbound>();
			services.AddSingleton<IEconomyOutbound>(sp => sp.GetRequiredService<EconomyOutbound>());
			services.AddSingleton<TradeOutbound>();
			services.AddSingleton<ITradeOutbound>(sp => sp.GetRequiredService<TradeOutbound>());
			services.AddSingleton<TradeService>();
			// The XP payout is a single-op seam onto LevelUpService (a quest award is a second SOURCE of XP, not a
			// second copy of the ding burst - see LevelUpService's header).
			services.AddSingleton(sp => new QuestRewardService(sp.GetRequiredService<IDatabaseRepository>(), sp.GetRequiredService<IQuestOutbound>(),
				sp.GetRequiredService<IEconomyOutbound>(), sp.GetRequiredService<LevelUpService>().ApplyXp, sp.GetRequiredService<ILogger>()));
			services.AddSingleton<DailyChallengeService>();
			// BIND: the post-grant collect-goal tick. EconomyOutbound is built above the quest services and needs one
			// op back from them, so it is wired the moment that op's owner exists.
			services.AddSingleton(sp =>
			{
				var progression = new QuestProgressionService(sp.GetRequiredService<IDatabaseRepository>(), sp.GetRequiredService<IQuestOutbound>(),
					sp.GetRequiredService<IEconomyOutbound>(), sp.GetRequiredService<TortageIntroService>(), sp.GetRequiredService<IChatServerDispatcher>(),
					sp.GetRequiredService<QuestRewardService>(), sp.GetRequiredService<DailyChallengeService>(), sp.GetRequiredService<ILogger>());
				sp.GetRequiredService<EconomyOutbound>().BindCollectGoals(progression.AdvanceCollectGoals);
				return progression;
			});

			// --- spells, buffs, combat --------------------------------------------------------------------------
			services.AddSingleton<SpellModifierService>();
			services.AddSingleton<BuffStatEffects>();
			services.AddSingleton<SpellPoolEffects>();
			services.AddSingleton<BuffService>();
			services.AddSingleton<PetService>();
			services.AddSingleton<SpellEffectOutbound>();
			services.AddSingleton<ISpellEffectOutbound>(sp => sp.GetRequiredService<SpellEffectOutbound>());
			services.AddSingleton<KnockbackService>();
			services.AddSingleton<SpellEffectService>();
			services.AddSingleton<FatalityService>();
			// The faction standing push is a static send, so the seam names it directly rather than an instance.
			services.AddSingleton(sp => new FactionService(sp.GetRequiredService<IDatabaseRepository>(), sp.GetRequiredService<ILogger>(),
				GameWire.SendFactionStanding));
			// The kill-streak table is a configured VALUE; the BIND re-seeds the level-up port's max-stat recompute.
			services.AddSingleton(sp =>
			{
				var combat = new CombatService(sp.GetRequiredService<WorldSimulation>(), sp.GetRequiredService<IDatabaseRepository>(),
					sp.GetRequiredService<QuestProgressionService>(), sp.GetRequiredService<ILogger>(), sp.GetRequiredService<LevelUpService>(),
					sp.GetRequiredService<FatalityService>(), sp.GetRequiredService<FactionService>(), sp.GetRequiredService<WorldBroadcaster>(),
					sp.GetRequiredService<ICsPlayerAgentDispatcher>(), sp.GetRequiredService<SpellPoolEffects>(),
					sp.GetRequiredService<IConfiguration>().ContentKillStreaks);
				sp.GetRequiredService<LevelUpOutbound>().BindReseed(combat.ReseedFromMaxStats);
				return combat;
			});
			services.AddSingleton<CastService>();
			services.AddSingleton<EnergyService>();
			services.AddSingleton<SpellweaveOutbound>();
			services.AddSingleton<ISpellweaveOutbound>(sp => sp.GetRequiredService<SpellweaveOutbound>());
			services.AddSingleton<SpellweaveService>();
			services.AddSingleton<ComboFinisher>();

			// --- travel, dialogue, teams ------------------------------------------------------------------------
			services.AddSingleton<TravelOutbound>();
			services.AddSingleton<ITravelOutbound>(sp => sp.GetRequiredService<TravelOutbound>());
			services.AddSingleton<TravelService>();
			services.AddSingleton<ConversationService>();
			services.AddSingleton<QuestShareOutbound>();
			services.AddSingleton<IQuestShareOutbound>(sp => sp.GetRequiredService<QuestShareOutbound>());
			services.AddSingleton<QuestSharingService>();
			services.AddSingleton<ApprenticeOutbound>();
			services.AddSingleton<IApprenticeOutbound>(sp => sp.GetRequiredService<ApprenticeOutbound>());
			services.AddSingleton<ApprenticeService>();
			services.AddSingleton<StockConversationService>();

			// --- inventory + economy services -------------------------------------------------------------------
			services.AddSingleton<LootService>();
			services.AddSingleton(sp => new KeepAliveService(sp.GetRequiredService<WorldSimulation>().Metrics));
			services.AddSingleton<ShortcutBarService>();
			services.AddSingleton<EquipmentService>();
			services.AddSingleton<ShopService>();
			services.AddSingleton<BankService>();
			services.AddSingleton<TradepostService>();
			services.AddSingleton<GuildBankService>();
			services.AddSingleton<TradeskillService>();
			services.AddSingleton<ClaimService>();
			// Two single-op seams: a feat's world reload is a teleport, and a feat that TEACHES grants a spell.
			services.AddSingleton(sp => new FeatService(sp.GetRequiredService<IDatabaseRepository>(), sp.GetRequiredService<IDatabaseRepository>(), sp.GetRequiredService<ILogger>(),
				sp.GetRequiredService<IChatServerDispatcher>(), (id, map, pos) => sp.GetRequiredService<TeleportService>().DoTeleport(id, map, pos),
				GameWire.SendLearnSpell, GameWire.SendForgetSpell));
			services.AddSingleton<SkillPointService>();

			// --- mounts + the environment -----------------------------------------------------------------------
			services.AddSingleton<MountService>();
			services.AddSingleton(sp => new EnvironmentOutbound(sp.GetRequiredService<BuffService>(), sp.GetRequiredService<MountService>().Dismount));
			services.AddSingleton<IEnvironmentOutbound>(sp => sp.GetRequiredService<EnvironmentOutbound>());
			services.AddSingleton<EnvironmentService>();

			// --- the tick's whole outbound surface, composed once everything it reaches exists -------------------
			services.AddSingleton(sp => new WorldOutbound(sp.GetRequiredService<WorldBroadcaster>(), sp.GetRequiredService<NpcBroadcaster>(),
				sp.GetRequiredService<LootBroadcaster>(), sp.GetRequiredService<WorldObjectService>(), sp.GetRequiredService<BuffService>(),
				sp.GetRequiredService<CombatService>(), sp.GetRequiredService<TradeskillService>(), sp.GetRequiredService<MountService>().Dismount));
			services.AddSingleton<IWorldOutbound>(sp => sp.GetRequiredService<WorldOutbound>());

			// The player stances, the achievement vistas and item use - single-domain listener partials until #928 item 6.
			services.AddSingleton<StanceService>();
			services.AddSingleton<MovementService>();
			services.AddSingleton<LoginHandshakeService>();
			services.AddSingleton<VistaService>();
			services.AddSingleton<ItemUseService>();

			// The `.command` GM table and handlers - last, because a GM command can reach nearly every service above.
			services.AddSingleton<GmCommandService>();

			return services;
		}
	}
}
