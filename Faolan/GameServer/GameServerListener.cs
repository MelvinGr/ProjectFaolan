using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Cli;
using Faolan.Core.Constants;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Faction;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Logging;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Network.Server.CsPlayerAgent;
using Faolan.Core.Network.Server.GameServer;
using Faolan.Core.Protobuf;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	// NOTE: this class implements NO outbound port any more (#826). Every one of them was a facade over the composed
	// services - WorldOutbound, EconomyOutbound, TradeOutbound, QuestShareOutbound, ApprenticeOutbound, LevelUpOutbound,
	// EnvironmentOutbound, TravelOutbound, SpellweaveOutbound and SpellEffectOutbound are all their own classes, built
	// in the ctor below and handed to the services that need them. It takes no `this` pass either.
	public partial class GameServerListener : RpcServer, IGameServerProvider
	{
		// Squared world-distance a move must cover to count as a real TRANSLATION that interrupts an in-flight cast
		// (#347). Above pure-rotation jitter (turning in place leaves the position unchanged) but well under one walk
		// step, so any genuine step cancels the cast while aiming a ground-target reticle does not.
		private const float CastMoveCancelEpsilonSq = 0.04f; // (0.2 world units)^2

		// The beach playfield id MapChange/BuildMapChange emits (0x004FE428); the spawn transform + world objects scope
		// to it. Threaded as one value so the snapshot/spawn/objects agree (per-map sourcing is a follow-up).
		private const uint BeachPlayfieldId = WorldState.DefaultPlayfieldToken;

		// The Tortage beach world-entry snapshot (0x737A6DF9) embeds quest 10238 in its tails for EVERY character
		// (verified across the Strie / hoxxling / randochar captures - it is the canonical beach starting quest, not
		// a per-character artifact). Since the snapshot is replayed and the client renders that quest, persist it so
		// the DB matches what the journal shows (otherwise CharacterQuests is empty while the journal shows a quest -
		// see snapshot_embeds_strie_quest). See CommitCharacterName / QuestContent.BeachStarterQuestId.
		private const uint BeachStarterQuestId = QuestContent.BeachStarterQuestId;

		// The 0x205A (PerformSpecialAction) selector for the "Path of Asura" teleport spell (NecroL80 path-of-azura
		// capture: PerformSpecialAction 0x4E1 -> retail DispatchRedirectToServer, a long-load teleport). It recalls to
		// the BOUND location (our single-server DoTeleport is the equivalent of retail's cross-server redirect).
		// ⚠️ Do NOT conflate it with the Totem of Origins: the Totem returns to the RACIAL home city instead
		// (StartingData.RacialHomeMap). Two different destinations, two different handlers.
		private const uint PathOfAsuraSpecialAction = 0x4E1;

		// The two stance selectors we answer (#505). Like every 0x205A selector these ARE the granted ability's
		// `Ability.Slot`: 0x064 is `Search` (100) and 0x068 is `Hide` (104).
		private const uint SearchSpecialAction = 0x064;
		private const uint SneakHideSpecialAction = 0x068;
		private const uint RestSpecialAction = 0x06A;

		// The COMBAT TOGGLE - sheathe / draw (#505). Its `Transition` animation action is per WEAPON and nothing we
		// hold maps a weapon to one, so the animation is deliberately NOT sent; what IS derivable is the half retail
		// pairs with it, the rating quad (see SendCombatRatings).
		private const uint CombatToggleSpecialAction = 0x4E2;


		// (The spellweave activate/deactivate selector used to live here as a single 0xFC constant. It is PER CLASS -
		// necromancer 0xFC, demonologist 0x10C - so it moved to StartingData.SpellweaveToggleAction, which carries the
		// corpus evidence for each row.)
		// The create-flow packets build each endpoint through Endpoints.Endpoint(type, id) with the client instance
		// (the character's real id) woven into the sender/receiver conversation id.

		// The instance-selection reply (#309) comes from GameCharAgent at instance 0x57, NOT the 0x58 nearly every other
		// GameCharAgent reply uses - taken from the one captured exchange (chat-annotated-4, the c2s 0x207B at frame
		// 10723 answered from 0xA9ECB95D/0x57 at 10726). Copying the 0x58 of its neighbours would have been the obvious
		// guess and is wrong.
		private static readonly ProtoEndpoint InstanceSelectionSender = Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x57);


		// Personal bank storage (kind 105) delivered on-demand. The withdraw item-grant op is an injected seam.
		// See Economy/BankService.cs.
		private readonly BankService _bank;

		// #826: everything the world tick sends or triggers. Was 42 explicit IWorldOutbound members on this class.
		private readonly WorldOutbound _worldOutbound;

		// #826: the quest-share wire, off this class and onto the ConnectionRegistry seam. Also the single owner of
		// the live-character view - GetCharacterLiveState below forwards here.
		private readonly QuestShareOutbound _questShare;

		private readonly EconomyOutbound _economyOutbound;
		// #367: what an active buff adds to its carrier's stats (armor/ratings/attributes), from the imported RDB
		// [2][9] stat effects. Recomputes `gear + buffs` whenever a buff lands or ends; see Combat/BuffStatEffects.cs.
		private readonly BuffStatEffects _buffStats;

		private readonly ClaimService _claims;
		private readonly QuestRewardService _questRewards;

		// Combat domain logic: melee/spell damage, NPC kill lifecycle, XP/leveling, kill-quest tracking + the combat
		// wire packets. The opcode dispatch (Handle0x2000) and GM .hurt/.heal call into it. See Combat/CombatService.cs.
		private readonly CombatService _combat;

		// Melee combos (#139): reads the combo id + step the client puts on every Combat:Melee swing and tracks the
		// running attempt, so the finisher can deliver the combo's own spell. See Combat/ComboService.cs.
		private readonly ComboService _combos;

		// NPC dialogue runtime (TalkToNpc/KnubotAnswer -> captured conversation nodes + quest grants). Composed
		// collaborator; uses the broadcaster for quest pushes + C# ConditionQuestId/State gates for branch conditions. See Dialogue/ConversationService.cs.
		private readonly ConversationService _conversation;

		// #310: Khitai reputation - awards standing on a kill and restores it at world entry.
		private readonly FactionService _factions;

		// The character-creation -> editor -> destiny-cutscene -> enter-world flow, extracted off this partial into its
		// own collaborator (CreateFlow/CreateFlowService.cs). The inbound create-flow handlers forward to it; the
		// burst-less world entry it needs is injected as a Func seam, and the handoff pair comes off _teleport.
		private readonly CreateFlowService _createFlow;

		// Persists equip/unequip/inventory-move/drop/split actions to the DB (no packet sends). The Inbound
		// handlers call into it. Composed collaborator, like CombatService. See Inventory/EquipmentService.cs.
		private readonly EquipmentService _equipment;

		// Composed collaborator: non-combat damage - fall damage + the scripted hazard volumes (#495/#504). See
		// World/EnvironmentService.cs.
		private readonly EnvironmentService _environment;

		// Composed collaborator: knockback - the server shoving an entity along a segment it did not walk (#496). See

		// Composed collaborator: local travel - the NPCs who move a player for a fee (#506). See World/TravelService.cs.
		private readonly TravelService _travel;
		private readonly FeatService _feats;

		// Composed collaborator: skillpoint spends - the c2s SkillIIR transfer out of the SkillPoints pool (#739).
		// See Progression/SkillPointService.cs.
		private readonly SkillPointService _skillPoints;

		// Composed collaborator: character advancement - XP in, levels out, and the whole level-up burst (#209). Every
		// path that moves a level goes through it (a kill's XP via CombatService, a GM `.level` set).
		// See Progression/LevelUpService.cs.
		private readonly LevelUpService _levelUp;

		// Composed collaborator: the BUFF ENGINE (#274/#367/#784) - apply, track, expire, remove, restore, and the stat
		// deltas a live buff carries. The three outbound ports whose buff members used to BE the implementation
		// (IWorldOutbound, ISpellEffectOutbound, ISpellweaveOutbound) now forward here. See Combat/BuffService.cs.
		private readonly BuffService _buffs;

		// Composed collaborator: the CAST PIPELINE (#236/#271/#767) - begin-ack, cast time, then resolve (server-side
		// effects, ground AoE, the landing buff, mana, instant areas, offensive damage). See Combat/CastService.cs.
		private readonly CastService _cast;

		// Composed collaborator: what a completed melee COMBO does when it lands (#139) - stamina, the finisher's
		// spell, its damage, its knockback. A layer ABOVE the combo catalog (_combos), which is why it is a separate
		// class - see Combat/ComboFinisher.cs. Built last: it needs the knockback service.
		private readonly ComboFinisher _comboFinisher;

		private readonly IChatServerDispatcher _agentServerDispatcher;
		private readonly IGameServerDispatcher _gameServerDispatcher;

		// Composed collaborator: the live player-trade sessions - see TradeService.
		private readonly TradeService _trade;

		// Seam into the CsPlayerAgent listener, which owns the team roster (ICsPlayerAgentProvider).
		private readonly ICsPlayerAgentDispatcher _csPlayerAgentDispatcher;

		// Connection keep-alive / latency replies (LatencyCheck 0x2085 -> Pong, Ping 0x2005 -> GameServerStats).
		// See Session/KeepAliveService.cs.
		private readonly KeepAliveService _keepAlive;

		// Loot-window flow (loot-all/single-drag/cash/close): orchestrates the broadcaster's bag state + the
		// item-grant/wallet ports. The loot Inbound cases delegate to it. See World/LootService.cs.
		private readonly LootService _loot;

		// Player-state persistence (autosave loop + logout/teleport/.save flushes), composed off the listener (#200).
		private readonly PlayerPersistenceService _persistence;

		// Scripted Tortage-intro narrative (Kalanthes create-cutscene walk + Casilda beach rescue), #353.
		private readonly TortageIntroService _tortageIntro;

		// The multi-stage quest-chain supersession engine (#261), shared by the kill/loot/talk triggers.
		private readonly QuestProgressionService _questProgression;

		// Quest SHARING (#101): reaches the team roster through the CS seam and the wire through IQuestShareOutbound.
		private readonly QuestSharingService _questSharing;
		private readonly ApprenticeService _apprentice;

		// NPC vendor / shop sell-buy-undo flow (owns the open-shop/last-sale state). The wallet-update + item-grant
		// outbound ops are injected as seams (shared with other subsystems). See Economy/ShopService.cs.
		private readonly ShopService _shop;

		// The player action bar / hotbar persistence + login restore (#71). See Inventory/ShortcutBarService.cs.
		private readonly ShortcutBarService _shortcutBar;

		// Crafting: turns a recipe's ingredients into its product. Reaches the wire through the same IEconomyOutbound
		// grant/take ops the shop and bank use. See Economy/TradeskillService.cs.
		private readonly GuildBankService _guildBank;
		private readonly TradeskillService _tradeskill;

		// Getting ON and OFF a mount (#26 / #142 / #428) - the seat, the persisted mount, and the dismount every
		// involuntary path shares. See World/MountService.cs.
		private readonly MountService _mounts;

		// A player dying, releasing and coming back: the death screen, the release timer, the self-resurrection a
		// spell can arm, and the respawn. See World/PlayerDeathService.cs.
		private readonly PlayerDeathService _playerDeaths;

		// Knockback: resolve the shove, apply retail's immunity buff, put the displacement on the wire.
		// See Combat/KnockbackService.cs.
		private readonly KnockbackService _knockback;

		// The loot LIFECYCLE's wire side - a corpse's drop, the bag's open/close/fade - and what is IN each bag.
		// See World/LootBroadcaster.cs.
		private readonly LootBroadcaster _lootBroadcaster;

		// What an NPC DOES, told to everyone who can see it - aggro, combat mode, movement, patrol, follow, respawn,
		// scripted exit - plus putting a runtime NPC or pet in the world. The broadcaster answers the other half of
		// the question, who can see what. See World/NpcBroadcaster.cs.
		private readonly NpcBroadcaster _npcs;

		// The quest WIRE (journal packets + the overhead '!' / '?' markers) and the marker DECISION behind it. Both
		// are shared: the broadcaster calls into the wire on every streamed-in NPC, and _questOutbound is the
		// IQuestOutbound port over this plus the broadcaster's three world-side members. See Quest/QuestBroadcaster.cs.
		private readonly QuestBroadcaster _questBroadcaster;
		private readonly QuestOutbound _questOutbound;

		// INTERACTING with a world object - doors, containers, benches, harvest nodes, quest pickups, dynel visual
		// sequences, the GM delete. Its subject is LiveWorldObject, which is what separates it from the broadcaster's
		// WorldEntity. See World/WorldObjectService.cs.
		private readonly WorldObjectService _worldObjects;

		// The CHANGEMAP handoff (GM .teleport, the recalls, the travel box, a zone line, a cross-map trip) plus the
		// two latches that have to survive it - the re-entry mark and the epic-difficulty pick - because the re-entry
		// arrives on a NEW connection. See World/TeleportService.cs.
		private readonly TeleportService _teleport;

		// A player's summoned PET (#307): summon, the timed variant's extend-or-summon, and the pet bar's dismiss.
		// Reached from the cast path through ISpellEffectOutbound and from the QueuePetCommand handler. See
		// Combat/PetService.cs.
		private readonly PetService _pets;

		// The per-session ENERGY pool + the sprint booleans (the yellow bar sprint drains and spellweaving spends).
		// Its state is entirely on INetworkClientSession, so it is a plain collaborator over per-connection state -
		// which is what let the loop leave this class. See Combat/EnergyService.cs.
		private readonly EnergyService _energy;

		private readonly SpellweaveService _spellweave;

		// The cutscene stock-phrase subtitle channel (Kalanthes's beach lines): answers PlayStockphraseAsSubtitle
		// (0x205E) with the captured remoteformat, distinct from the interactive _conversation. See Dialogue/StockConversationService.cs.
		private readonly StockConversationService _stockConversation;

		// The auction house + the mailbox (listings, buy/sell, mail send/take). Reaches the wallet + item-grant
		// through IEconomyOutbound, like the shop and the bank. See Economy/TradepostService.cs.
		private readonly TradepostService _tradepost;

		// Builds + sends the world-event packets (spawns/moves/despawns, NPC respawn/loot/mount renders, loot-window
		// and quest-journal packets). Reads the world model via the simulation; the listener delegates its IWorldOutbound
		// port + the loot/quest opcode handlers to it. See World/WorldBroadcaster.cs.
		private readonly WorldBroadcaster _broadcaster;

		// The live world simulation: owns the world model, the connected players' authoritative entities, and the
		// 10 Hz tick (NPC respawn/AI, vitals regen, player death/respawn, resurrection discovery). The listener is its
		// IWorldOutbound port is handed to it at Start, not at construction. See World/WorldSimulation.cs.
		private readonly WorldSimulation _simulation;

		// World entry - the login/teleport/create ready bursts, the snapshot, the spawn poll - is a composed service
		// since the world-entry move (the last behaviour this listener owned that a service needed; see
		// CreateFlowService, which used to reach it through a bound seam).
		private readonly WorldEntryService _worldEntry;

		// The GM-bendable game clock the GameTime sends read (RealmClock).
		private readonly RealmClock _clock;

		// The `.command` table and its handlers (Gm/GmCommandService); the listener only routes a chat line there.
		private readonly GmCommandService _gmCommands;

		// Achievement tags (the vista award goes through it).
		private readonly TagService _tags;

		// The three former partials of this class that were single-domain (#928 item 6).
		private readonly StanceService _stances;
		private readonly VistaService _vista;
		private readonly ItemUseService _itemUse;

		// The travel confirmation box + the ACG teleport choice (TravelOutbound owns the confirm cookie).
		private readonly TravelOutbound _travelOutbound;

		// Background loops (persistence + simulation each own their own loop) and their shared lifetime token. Held
		// (not fire-and-forget) so StopAsync can signal them and await a clean shutdown, and so any unobserved fault
		// surfaces instead of being swallowed.
		private CancellationTokenSource _loopCts = null!;

		// Everything below arrives by INJECTION (#826): the ~61 services this ctor used to hand-`new` are registered
		// in GameServerRealmServices.AddRealmGameServer, and the fourteen that were held only in order to build
		// something else are not fields here at all any more.
		//
		// ⚠️ It takes an ILoggerFactory rather than an ILogger because the category is REALM-TAGGED
		// ("...GameServerListener-<realmId>"), and the base initializer has to build it before `Logger` exists. Every
		// composed service gets the same category through the bare `ILogger` the realm registration provides.
		//
		// ⚠️ The parameter count is the honest size of this class's collaborator surface, not an artifact of DI - it
		// is what the partials below actually call. Shrinking it means moving handlers out, which is #826's remaining
		// direction of travel, not hiding them behind a parameter object.
		public GameServerListener(ILoggerFactory loggerFactory, IConfiguration configuration, IDatabaseRepository database, HostRealm realm,
			IChatServerDispatcher agentServerDispatcher, IGameServerDispatcher gameServerDispatcher, ICsPlayerAgentDispatcher csPlayerAgentDispatcher,
			ConnectionRegistry connections,
			WorldSimulation simulation, PlayerDeathService playerDeaths, PlayerPersistenceService persistence, TortageIntroService tortageIntro, ComboService combos,
			QuestBroadcaster questBroadcaster, WorldBroadcaster broadcaster, NpcBroadcaster npcs, LootBroadcaster lootBroadcaster,
			QuestOutbound questOutbound, WorldObjectService worldObjects, TeleportService teleport, CreateFlowService createFlow,
			LevelUpService levelUp, EconomyOutbound economyOutbound, TradeService trade, QuestRewardService questRewards,
			QuestProgressionService questProgression, BuffStatEffects buffStats, BuffService buffs, PetService pets,
			KnockbackService knockback, FactionService factions, CombatService combat, CastService cast, EnergyService energy,
			SpellweaveService spellweave, TravelService travel, ConversationService conversation, QuestShareOutbound questShare,
			QuestSharingService questSharing, ApprenticeService apprentice, StockConversationService stockConversation, LootService loot,
			KeepAliveService keepAlive, ShortcutBarService shortcutBar, EquipmentService equipment, ShopService shop, BankService bank,
			TradepostService tradepost, GuildBankService guildBank, TradeskillService tradeskill, ClaimService claims, FeatService feats,
			SkillPointService skillPoints, MountService mounts, EnvironmentService environment, ComboFinisher comboFinisher,
			WorldOutbound worldOutbound, WorldEntryService worldEntry, RealmClock clock, GmCommandService gmCommands, TagService tags, StanceService stances, VistaService vista, ItemUseService itemUse, TravelOutbound travelOutbound)
			: base(realm.Port, RealmLogger(loggerFactory, realm), configuration, database, new GameServerPacketTracer(RealmLogger(loggerFactory, realm)), connections)
		{
			_agentServerDispatcher = agentServerDispatcher;
			_gameServerDispatcher = gameServerDispatcher;
			_csPlayerAgentDispatcher = csPlayerAgentDispatcher;

			_simulation = simulation;
			_playerDeaths = playerDeaths;
			_persistence = persistence;
			_tortageIntro = tortageIntro;
			_combos = combos;
			_questBroadcaster = questBroadcaster;
			_broadcaster = broadcaster;
			_npcs = npcs;
			_lootBroadcaster = lootBroadcaster;
			_questOutbound = questOutbound;
			_worldObjects = worldObjects;
			_teleport = teleport;
			_createFlow = createFlow;
			_levelUp = levelUp;
			_economyOutbound = economyOutbound;
			_trade = trade;
			_questRewards = questRewards;
			_questProgression = questProgression;
			_buffStats = buffStats;
			_buffs = buffs;
			_pets = pets;
			_knockback = knockback;
			_factions = factions;
			_combat = combat;
			_cast = cast;
			_energy = energy;
			_spellweave = spellweave;
			_travel = travel;
			_conversation = conversation;
			_questShare = questShare;
			_questSharing = questSharing;
			_apprentice = apprentice;
			_stockConversation = stockConversation;
			_loot = loot;
			_keepAlive = keepAlive;
			_shortcutBar = shortcutBar;
			_equipment = equipment;
			_shop = shop;
			_bank = bank;
			_tradepost = tradepost;
			_guildBank = guildBank;
			_tradeskill = tradeskill;
			_claims = claims;
			_feats = feats;
			_skillPoints = skillPoints;
			_mounts = mounts;
			_environment = environment;
			_comboFinisher = comboFinisher;
			_worldOutbound = worldOutbound;
			_worldEntry = worldEntry;
			_clock = clock;
			_gmCommands = gmCommands;
			_tags = tags;
			_stances = stances;
			_vista = vista;
			_itemUse = itemUse;
			_travelOutbound = travelOutbound;

		}

		// The realm-tagged log category, resolved twice in the base initializer above (once for the listener, once for
		// its packet tracer) - which costs nothing, because ILoggerFactory caches by category name and both calls
		// therefore return the same ILogger instance.
		private static ILogger RealmLogger(ILoggerFactory loggerFactory, HostRealm realm)
		{
			return loggerFactory.CreateLogger($"{typeof(GameServerListener).FullName}-{realm.Id}");
		}

		public bool DisconnectCharacter(uint characterId)
		{
			var client = ClientForCharacter(characterId);
			if (client == null)
				return false;

			client.Stop();
			return true;
		}

		// IGameServerProvider (the MCP session tool). One implementation, on QuestShareOutbound - this used to be a
		// second copy of the same world-model walk.
		public CharacterLiveState? GetCharacterLiveState(uint characterId)
		{
			return _questShare.LiveStateOf(characterId);
		}

		public IReadOnlyList<ZoneEntityInfo> GetZoneEntities(uint mapId)
		{
			// Observer view: union every live instance (copy) of the map (#268), so the MCP tool sees all entities
			// regardless of which copy they're in.
			return _simulation
				.World.ZonesForMap(mapId)
				.SelectMany(z => z.Entities)
				.Select(e => new ZoneEntityInfo(e.Handle, e.Name, e.IsPlayer
					? "player"
					: e.IsLoot
						? "loot"
						: "npc", e.Level, e.Hostile, e.IsDead, e.Vitals?.Health, e.Vitals?.MaxHealth, e.Position.X, e.Position.Y, e.Position.Z))
				.OrderBy(e => e.Handle)
				.ToArray();
		}

		// The CS tier's entry into a world teleport (#511: the group finder putting a formed team into its
		// instance). The same DoTeleport the travel box and the GM path resume into, so the retail change-map
		// sequence (announce + handoff + reconnect) is the only teleport shape this server has.
		public async Task<bool> TeleportCharacter(uint characterId, uint mapId, System.Numerics.Vector3? position = null)
		{
			if (ClientForCharacter(characterId) == null)
				return false;

			await _teleport.DoTeleport(characterId, mapId, position);
			return true;
		}

		public async Task<bool> RunGmCommand(uint characterId, string commandText)
		{
			var client = ClientForCharacter(characterId);
			if (client == null)
				return false;

			// GmCommandService.Handle strips a single leading prefix char (".teleport" -> "teleport"), so ensure one is present.
			if (commandText.Length == 0 || char.IsLetterOrDigit(commandText[0]))
				commandText = "." + commandText;

			await _gmCommands.Handle(client, commandText);
			return true;
		}

		public override Task StartAsync(CancellationToken cancellationToken)
		{
			_gameServerDispatcher.Register(this);
			// Own lifetime token (linked to startup) so StopAsync can cancel the loops independently of any single
			// connection. Tasks are stored so they can be awaited on shutdown.
			_loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			_persistence.Start(_loopCts.Token); // periodic crash-safety flush of the live world state
			_simulation.Start(_loopCts.Token, _worldOutbound); // the simulation tick (regen, NPC AI, death/respawn, discovery)
			return base.StartAsync(cancellationToken);
		}

		public override async Task StopAsync(CancellationToken cancellationToken)
		{
			_gameServerDispatcher.Unregister(this);

			// Signal the background loops and await their (clean) exit so shutdown doesn't race a mid-tick save. The
			// loops swallow their own OperationCanceledException, so WhenAll completes normally; bounded by the host's
			// shutdown token either way.
			if (_loopCts != null)
				await _loopCts.CancelAsync();
			await Task.WhenAll(_simulation.StopAsync(), _persistence.StopAsync());
			_loopCts?.Dispose();

			await base.StopAsync(cancellationToken);
		}

		protected override async Task ClientDisconnected(INetworkClient client)
		{
			var entity = _simulation.PlayerWorldEntity(client);
			if (entity != null)
			{
				// Tell everyone who could see this player that it left (n3ToClientQuit) BEFORE removing it from the
				// zone, so ResolveRemove still sees the leaver's viewers - now awaited (ClientDisconnected is a Task), 				// so the despawn deterministically lands before RemovePlayer instead of racing it.
				await _broadcaster.BroadcastPlayerDespawn(entity);
				// #907: a zone change tears this connection down and the re-entry builds a NEW entity (PlayerRegistry
				// is keyed by connection), so hold the live buffs over the handoff. Without it a debuff live at the
				// moment of the teleport has nothing left to expire it, and the client - which keeps its own icon
				// list across a map change - shows it at 0s for ever. Only on a teleport-out: on a real logout the
				// buffs are meant to end, and the client rebuilds its icon list from the next login's snapshot.
				if (client.Session.IsTeleportingOut)
					_teleport.CarryBuffs(entity.Handle, entity.ActiveBuffs);

				_simulation.RemovePlayer(client, out _);
				// Persist the player's final live state - movement isn't otherwise saved, so without this a logout
				// loses where they were. EXCEPT on a teleport-out: DoTeleport already saved the destination, and this
				// connection's entity still holds the stale pre-teleport position which would clobber it (#28).
				if (!client.Session.IsTeleportingOut)
					await _persistence.SavePlayerState(client, entity);
				else
					Logger.LogInformation("Teleport-out: skipping stale-position save for {Name} (0x{Handle:X8}); destination already persisted.", entity.Name, entity.Handle);

				client.Session.IsTeleportingOut = false; // one-shot: only suppress this teleport's disconnect save
				Logger.LogInformation("World: player {Name} (0x{Handle:X8}) left map {Map}.", entity.Name, entity.Handle, entity.MapId);
			}

			// Nor can a tradeskill mid-bar (#793): it is staged and un-run, so dropping it takes nothing from the
			// player - where letting it complete would grant an item to a session that has gone.
			if (_tradeskill.Forget(client) is var dropped and > 0)
				Logger.LogInformation("Tradeskill: dropped {Count} staged transaction(s) for a leaving connection.", dropped);

			// A trade cannot outlive either connection. Nothing has moved at this point - the exchange only happens on the
			// double accept - so there is nothing to roll back, just state to drop.
			if (client.Session.Character is { } tradingCharacter)
			{
				_trade.Abandon(tradingCharacter.Id);
				// Same rule for a quest share offered TO this player: the window is gone with the connection, so the
				// offer must be too - otherwise a reconnect could accept one made minutes ago on the other side of the map.
				_questSharing.Forget(tradingCharacter.Id);
				// And every apprenticeship they are in, on either side: the level boost is a pushed stat, so a bond
				// that survived a disconnect would leave the OTHER party's client holding it (#144).
				_ = _apprentice.Forget(tradingCharacter.Id);
			}

			// A logout also leaves the team - otherwise the roster keeps a member nobody can reach, and the next join
			// broadcast pushes at a dead connection. Retail dissolves a one-member team, which TeamService does too.
			if (client.Session.Character is { } character)
				await _csPlayerAgentDispatcher.LeaveTeam(character.Id);

			await base.ClientDisconnected(client);
		}

		protected override Task ReceivedPacket(INetworkClient client, Rpc packet)
		{
			// Lift the parsed frame into the structured Rpc the handlers dispatch on (Gitea #407); the total wire length
			// (which the Rpc doesn't carry) is threaded to the dispatchers that log it as packetLength.

			// This connection multiplexes several RPC classes whose 0x20xx method ids COLLIDE with ordinary opcodes, 			// so resolve the RPC CLASS (by receiver; for Tradepost also its sender) BEFORE the opcode switch - each
			// class route consumes the packet. See CLAUDE.md (wire-protocol essentials) + docs/subsystems/tradepost-protocol.md.
			// The two sub-0x2014 opcodes 0x2002/0x2003 are the ONLY ones that also arrive on GameCharAgent (there they
			// are RequestBindLocation / SetTutorialStats); the GameAgent case owns the opcode dispatch for the rest.

			// The ONE reader over the body for this packet's whole dispatch chain: BodyReader() hands out a fresh cursor
			// per call, so every layer (dispatcher -> handler -> service) shares this instance and reads from its
			// position rather than re-reading from 0. Threaded down as a `FaolanStream body` parameter. Handlers that
			// read a sub-header first (HandleIirCarrier) leave the cursor advanced for their own sub-handlers.
			var body = packet.BodyReader();

			return packet.Receiver.Type switch
			{
				// Tradepost (auction house + mail): inbound packets address the TradepostAgent endpoint.
				RpcInterfaces.TradepostAgentType => HandleTradepostPacket(client, packet, body),

				// GameCharAgentProject ("post-ready family"): the in-world RequestAggroStatus poll GATES the loading
				// screen (without the reply the client retries forever + never opens chat).
				//
				// Routed on EVERY connection role, create flows included (#564). It used to be gated to an existing
				// character's world login, on the belief that "the create flows own this class during their own
				// handshake" - they do not: the create-editor's handshake packets are on GameAgent, and what this
				// gate actually dropped was the poll the client fires at the five editor scene characters as soon as
				// they are streamed (before the name is committed). Retail answers those - 55/55 in
				// account-stricken998-character-create-no-worldenter, 95/95 in Barbarian-create - so we do too.
				RpcInterfaces.GameCharAgentProjectType => HandleGameCharAgentProject(client, packet, body),

				// GameCharAgent: the 0x2002/0x2003 collision forms (bind/tutorial) PLUS every per-character method
				// (loot / shop / respawn / shortcut / latency / ...) - the loopback confirmed these all arrive here.
				RpcInterfaces.GameCharAgentType => HandleGameCharAgentPacket(client, packet, body),

				// GameAgent (the connection class): auth / chat / ping / server-stats (+ VanityToggle, GCAProject-ish).
				RpcInterfaces.GameAgentType => HandleGameAgentPacket(client, packet, body),

				_ => throw new Exception($"Unhandled packet receiver type: {packet.Receiver.Type:X8}")
			};
		}

		// Tradepost/mail packets are the service's (Economy/TradepostService.cs); the listener keeps only the
		// unknown-packet tracer, which is connection bookkeeping rather than economy logic.
		private async Task HandleTradepostPacket(INetworkClient client, Rpc packet, FaolanStream body)
		{
			if (!await _tradepost.Handle(client, packet, body))
				LogUnknownPacket(packet);
		}


	}
}
