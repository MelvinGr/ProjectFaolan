using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Constants;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Logging;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Network.Server.CsPlayerAgent;
using Faolan.Core.Network.Server.GameServer;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Apprentice;
using Faolan.GameServer.Behavior;
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
	/// <remarks>
	///     This class implements no outbound port. WorldOutbound, EconomyOutbound, TradeOutbound, QuestShareOutbound,
	///     ApprenticeOutbound, LevelUpOutbound, EnvironmentOutbound, TravelOutbound, SpellweaveOutbound and
	///     SpellEffectOutbound are each their own class, built in the constructor below and handed to the services that
	///     need them; nothing is handed <c>this</c>.
	/// </remarks>
	public partial class GameServerListener : RpcServer, IGameServerProvider
	{
		// The 0x205A (PerformSpecialAction) selector for the "Path of Asura" teleport spell (NecroL80 path-of-azura
		// capture: PerformSpecialAction 0x4E1 -> retail DispatchRedirectToServer, a long-load teleport). It recalls to
		// the bound location; DoTeleport is the single-server equivalent of retail's cross-server redirect.
		// Not to be confused with the Totem of Origins, which returns to the racial home city instead
		// (StartingData.RacialHomeMap) - two different destinations, two different handlers.
		private const uint PathOfAsuraSpecialAction = 0x4E1;

		// The two answered stance selectors. Like every 0x205A selector these are the granted ability's `Ability.Slot`:
		// 0x064 is `Search` (100) and 0x068 is `Hide` (104).
		private const uint SearchSpecialAction = 0x064;
		private const uint SneakHideSpecialAction = 0x068;
		private const uint RestSpecialAction = 0x06A;

		// The combat toggle - sheathe / draw. Its `Transition` animation action is per weapon and no imported data maps
		// a weapon to one, so the animation is deliberately not sent; the derivable half retail pairs with it is the
		// rating quad (see SendCombatRatings).
		private const uint CombatToggleSpecialAction = 0x4E2;

		// The spellweave activate/deactivate selector is per class (necromancer 0xFC, demonologist 0x10C), so it lives
		// in StartingData.SpellweaveToggleAction, which carries the corpus evidence for each row.
		// The create-flow packets build each endpoint through Endpoints.Endpoint(type, id) with the client instance
		// (the character's real id) woven into the sender/receiver conversation id.

		// The instance-selection reply comes from GameCharAgent at instance 0x57, not the 0x58 nearly every other
		// GameCharAgent reply uses - per the one captured exchange (chat-annotated-4, the c2s 0x207B at frame 10723
		// answered from 0xA9ECB95D/0x57 at 10726).
		private static readonly ProtoEndpoint InstanceSelectionSender = Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x57);

		// Personal bank storage (kind 105) delivered on-demand. The withdraw item-grant op is an injected seam.
		// See Economy/BankService.cs.
		private readonly BankService _bank;

		// Everything the world tick sends or triggers.
		private readonly WorldOutbound _worldOutbound;

		// The quest-share wire, over the ConnectionRegistry seam. Also the single owner of the live-character view -
		// GetCharacterLiveState below forwards here.
		private readonly QuestShareOutbound _questShare;

		private readonly EconomyOutbound _economyOutbound;

		private readonly ClaimService _claims;
		private readonly QuestRewardService _questRewards;

		// NPC dialogue runtime (TalkToNpc/KnubotAnswer -> captured conversation nodes + quest grants). Composed
		// collaborator; uses the broadcaster for quest pushes + C# ConditionQuestId/State gates for branch conditions. See Dialogue/ConversationService.cs.
		private readonly ConversationService _conversation;

		// The character-creation -> editor -> destiny-cutscene -> enter-world flow (CreateFlow/CreateFlowService.cs).
		// The inbound create-flow handlers forward to it; it takes the burst-less world entry and the handoff pair as
		// constructor dependencies.
		private readonly CreateFlowService _createFlow;

		// Persists equip/unequip/inventory-move/drop/split actions to the DB (no packet sends). The Inbound
		// handlers call into it. Composed collaborator, like CombatService. See Inventory/EquipmentService.cs.
		private readonly EquipmentService _equipment;

		private readonly FeatService _feats;

		// Skill-point spends - the c2s SkillIIR transfer out of the SkillPoints pool.
		// See Progression/SkillPointService.cs.
		private readonly SkillPointService _skillPoints;

		// The buff engine - apply, track, expire, remove, restore, and the stat deltas a live buff carries. The buff
		// members of IWorldOutbound, ISpellEffectOutbound and ISpellweaveOutbound all forward here.
		// See Combat/BuffService.cs.
		private readonly BuffService _buffs;

		private readonly IChatServerDispatcher _agentServerDispatcher;
		private readonly IGameServerDispatcher _gameServerDispatcher;

		// The live player-trade sessions - see TradeService.
		private readonly TradeService _trade;

		// Seam into the CsPlayerAgent listener, which owns the team roster (ICsPlayerAgentProvider).
		private readonly ICsPlayerAgentDispatcher _csPlayerAgentDispatcher;

		// Connection keep-alive / latency replies (LatencyCheck 0x2085 -> Pong, Ping 0x2005 -> GameServerStats).
		// See Session/KeepAliveService.cs.
		private readonly KeepAliveService _keepAlive;

		// Loot-window flow (loot-all/single-drag/cash/close): orchestrates the broadcaster's bag state + the
		// item-grant/wallet ports. The loot Inbound cases delegate to it. See World/LootService.cs.
		private readonly LootService _loot;

		// Player-state persistence: the autosave loop plus the logout/teleport/.save flushes.
		private readonly PlayerPersistenceService _persistence;

		// Quest sharing: reaches the team roster through the CS seam and the wire through IQuestShareOutbound.
		private readonly QuestSharingService _questSharing;
		private readonly ApprenticeService _apprentice;

		// NPC vendor / shop sell-buy-undo flow (owns the open-shop/last-sale state). The wallet-update + item-grant
		// outbound ops are injected as seams (shared with other subsystems). See Economy/ShopService.cs.
		private readonly ShopService _shop;

		// The player action bar / hotbar persistence + login restore. See Inventory/ShortcutBarService.cs.
		private readonly ShortcutBarService _shortcutBar;

		private readonly GuildBankService _guildBank;

		// Crafting: turns a recipe's ingredients into its product. Reaches the wire through the same IEconomyOutbound
		// grant/take ops the shop and bank use. See Economy/TradeskillService.cs.
		private readonly TradeskillService _tradeskill;

		// A player dying, releasing and coming back: the death screen, the release timer, the self-resurrection a
		// spell can arm, and the respawn. See World/PlayerDeathService.cs.
		private readonly PlayerDeathService _playerDeaths;

		// The quest wire (journal packets + the overhead '!' / '?' markers) and the marker decision behind it. Shared:
		// the broadcaster calls into the wire on every streamed-in NPC, and QuestOutbound is the IQuestOutbound port
		// over this plus the broadcaster's three world-side members. See Quest/QuestBroadcaster.cs.
		private readonly QuestBroadcaster _questBroadcaster;

		// The changemap handoff (GM .teleport, the recalls, the travel box, a zone line, a cross-map trip) plus the two
		// latches that have to survive it - the re-entry mark and the epic-difficulty pick - because the re-entry
		// arrives on a new connection. See World/TeleportService.cs.
		private readonly TeleportService _teleport;

		// A player's summoned pet: summon, the timed variant's extend-or-summon, and the pet bar's dismiss. Reached
		// from the cast path through ISpellEffectOutbound and from the QueuePetCommand handler.
		// See Combat/PetService.cs.
		private readonly PetService _pets;

		// The per-session energy pool + the sprint booleans (the yellow bar sprint drains and spellweaving spends). Its
		// state lives entirely on INetworkClientSession, so it is a plain collaborator over per-connection state.
		// See Combat/EnergyService.cs.
		private readonly EnergyService _energy;

		private readonly SpellweaveService _spellweave;

		// The cutscene stock-phrase subtitle channel (Kalanthes's beach lines): answers PlayStockphraseAsSubtitle
		// (0x205E) with the captured remoteformat, distinct from the interactive _conversation. See Dialogue/StockConversationService.cs.
		private readonly StockConversationService _stockConversation;

		// The auction house + the mailbox (listings, buy/sell, mail send/take). Reaches the wallet + item-grant
		// through IEconomyOutbound, like the shop and the bank. See Economy/TradepostService.cs.
		private readonly TradepostService _tradepost;

		// Builds + sends the world-event packets (spawns/moves/despawns, NPC respawn/loot/mount renders, loot-window
		// and quest-journal packets). Reads the world model via the simulation. See World/WorldBroadcaster.cs.
		private readonly WorldBroadcaster _broadcaster;

		// The live world simulation: owns the world model, the connected players' authoritative entities, and the 10 Hz
		// tick (NPC respawn/AI, vitals regen, player death/respawn, resurrection discovery). Its IWorldOutbound port is
		// handed to it at Start, not at construction. See World/WorldSimulation.cs.
		private readonly WorldSimulation _simulation;

		// World entry - the login/teleport/create ready bursts, the snapshot, the spawn poll. CreateFlowService takes
		// it as an ordinary dependency, which is why it is a service rather than a partial of this class.
		private readonly WorldEntryService _worldEntry;

		// The GM-bendable game clock the GameTime sends read (RealmClock).
		private readonly RealmClock _clock;

		// The `.command` table and its handlers (Gm/GmCommandService); the listener only routes a chat line there.
		private readonly GmCommandService _gmCommands;

		private readonly StanceService _stances;
		private readonly VistaService _vista;

		// The player move and the Authenticate2 hello - the two inbound cases that are handlers rather than forwards.
		private readonly MovementService _movement;
		private readonly BehaviorService _behavior;
		private readonly LoginHandshakeService _login;

		// Background loops (persistence + simulation each own their own loop) and their shared lifetime token. Held
		// (not fire-and-forget) so StopAsync can signal them and await a clean shutdown, and so any unobserved fault
		// surfaces instead of being swallowed.
		private CancellationTokenSource _loopCts = null!;

		// Every collaborator arrives by injection; they are registered in
		// GameServerRealmServices.AddRealmGameServer.
		//
		// The constructor takes an ILoggerFactory rather than an ILogger because the category is realm-tagged
		// ("...GameServerListener-<realmId>") and the base initializer has to build it before `Logger` exists. Every
		// composed service gets the same category through the bare `ILogger` the realm registration provides.
		//
		// The parameter count is the size of this class's collaborator surface - what the partials below actually call
		// - rather than an artifact of DI. Shrinking it means moving handlers out, not hiding them behind a parameter
		// object.
		public GameServerListener(ILoggerFactory loggerFactory, IConfiguration configuration, IDatabaseRepository database, HostRealm realm,
			IChatServerDispatcher agentServerDispatcher, IGameServerDispatcher gameServerDispatcher,
			ICsPlayerAgentDispatcher csPlayerAgentDispatcher, ConnectionRegistry connections, WorldSimulation simulation,
			PlayerDeathService playerDeaths, PlayerPersistenceService persistence, QuestBroadcaster questBroadcaster, WorldBroadcaster broadcaster,
			TeleportService teleport, CreateFlowService createFlow, EconomyOutbound economyOutbound, TradeService trade,
			QuestRewardService questRewards, BuffService buffs, PetService pets, EnergyService energy, SpellweaveService spellweave,
			ConversationService conversation, QuestShareOutbound questShare, QuestSharingService questSharing, ApprenticeService apprentice,
			StockConversationService stockConversation, LootService loot, KeepAliveService keepAlive, ShortcutBarService shortcutBar,
			EquipmentService equipment, ShopService shop, BankService bank, TradepostService tradepost, GuildBankService guildBank,
			TradeskillService tradeskill, ClaimService claims, FeatService feats, SkillPointService skillPoints, WorldOutbound worldOutbound,
			WorldEntryService worldEntry, RealmClock clock, GmCommandService gmCommands, StanceService stances, VistaService vista,
			MovementService movement, LoginHandshakeService login, BehaviorService behavior)
			: base(realm.Port, RealmLogger(loggerFactory, realm), configuration, database, new GameServerPacketTracer(RealmLogger(loggerFactory, realm)), connections)
		{
			_agentServerDispatcher = agentServerDispatcher;
			_gameServerDispatcher = gameServerDispatcher;
			_csPlayerAgentDispatcher = csPlayerAgentDispatcher;

			_simulation = simulation;
			_playerDeaths = playerDeaths;
			_persistence = persistence;
			_questBroadcaster = questBroadcaster;
			_broadcaster = broadcaster;
			_teleport = teleport;
			_createFlow = createFlow;
			_economyOutbound = economyOutbound;
			_trade = trade;
			_questRewards = questRewards;
			_buffs = buffs;
			_pets = pets;
			_energy = energy;
			_spellweave = spellweave;
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
			_worldOutbound = worldOutbound;
			_worldEntry = worldEntry;
			_clock = clock;
			_gmCommands = gmCommands;
			_stances = stances;
			_vista = vista;
			_movement = movement;
			_behavior = behavior;
			_login = login;

		}

		// The realm-tagged log category, resolved twice in the base initializer above (once for the listener, once for
		// its packet tracer). That costs nothing: ILoggerFactory caches by category name, so both calls return the same
		// ILogger instance.
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

		// IGameServerProvider (the MCP session tool). The world-model walk has one implementation, on
		// QuestShareOutbound.
		public CharacterLiveState? GetCharacterLiveState(uint characterId)
		{
			return _questShare.LiveStateOf(characterId);
		}

		public IReadOnlyList<ZoneEntityInfo> GetZoneEntities(uint mapId)
		{
			// Observer view: union every live instance (copy) of the map, so the MCP tool sees all entities regardless
			// of which copy they are in.
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

		// The CS tier's entry into a world teleport (the group finder putting a formed team into its instance). The
		// same DoTeleport the travel box and the GM path resume into, so the retail change-map sequence (announce +
		// handoff + reconnect) is the only teleport shape this server has.
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
				// Tell everyone who could see this player that it left (n3ToClientQuit) before removing it from the
				// zone, so ResolveRemove still sees the leaver's viewers. Awaited, so the despawn lands before
				// RemovePlayer rather than racing it.
				await _broadcaster.BroadcastPlayerDespawn(entity);
				// A zone change tears this connection down and the re-entry builds a new entity (PlayerRegistry is
				// keyed by connection), so the live buffs are held over the handoff. Without that, a debuff live at the
				// moment of the teleport has nothing left to expire it and the client - which keeps its own icon list
				// across a map change - shows it at 0 s for ever. Only on a teleport-out: on a real logout the buffs
				// are meant to end, and the client rebuilds its icon list from the next login's snapshot.
				if (client.Session.IsTeleportingOut)
					_teleport.CarryBuffs(entity.Handle, entity.ActiveBuffs);

				_simulation.RemovePlayer(client, out _);
				// Persist the player's final live state - movement is not otherwise saved, so without this a logout
				// loses where they were. Except on a teleport-out: DoTeleport has already saved the destination, and
				// this connection's entity still holds the stale pre-teleport position, which would clobber it.
				if (!client.Session.IsTeleportingOut)
					await _persistence.SavePlayerState(client, entity);
				else
					Logger.LogInformation("Teleport-out: skipping stale-position save for {Name} (0x{Handle:X8}); destination already persisted.", entity.Name, entity.Handle);

				client.Session.IsTeleportingOut = false; // one-shot: only suppress this teleport's disconnect save
				Logger.LogInformation("World: player {Name} (0x{Handle:X8}) left map {Map}.", entity.Name, entity.Handle, entity.MapId);
			}

			// A tradeskill mid-bar cannot outlive the connection either: it is staged and un-run, so dropping it takes
			// nothing from the player, where letting it complete would grant an item to a session that has gone.
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
				_questBroadcaster.ForgetMarkerContext(tradingCharacter.Id); // the cached quest-marker state leaves with the session
				// And every apprenticeship they are in, on either side: the level boost is a pushed stat, so a bond
				// that survived a disconnect would leave the other party's client holding it.
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
			// This connection multiplexes several RPC classes whose 0x20xx method ids collide with ordinary opcodes, so
			// the RPC class is resolved first (by receiver; for Tradepost also by sender) and each class route consumes
			// the packet. See CLAUDE.md (wire-protocol essentials) + docs/subsystems/tradepost-protocol.md. The two
			// sub-0x2014 opcodes 0x2002/0x2003 are the only ones that also arrive on GameCharAgent (there they are
			// RequestBindLocation / SetTutorialStats); the GameAgent case owns the opcode dispatch for the rest.
			//
			// One reader over the body serves this packet's whole dispatch chain: BodyReader() hands out a fresh cursor
			// per call, so every layer (dispatcher -> handler -> service) shares this instance and reads from its
			// position rather than re-reading from 0. It is threaded down as a `FaolanStream body` parameter. Handlers
			// that read a sub-header first (HandleIirCarrier) leave the cursor advanced for their own sub-handlers.
			var body = packet.BodyReader();

			return packet.Receiver.Type switch
			{
				// Tradepost (auction house + mail): inbound packets address the TradepostAgent endpoint.
				RpcInterfaces.TradepostAgentType => HandleTradepostPacket(client, packet, body),

				// GameCharAgentProject ("post-ready family"): the in-world RequestAggroStatus poll gates the loading
				// screen (without the reply the client retries forever and never opens chat).
				//
				// Routed on every connection role, create flows included: the create-editor's handshake packets are on
				// GameAgent, and the client fires this poll at the five editor scene characters as soon as they are
				// streamed, before the name is committed. Retail answers those - 55/55 in
				// account-stricken998-character-create-no-worldenter, 95/95 in Barbarian-create.
				RpcInterfaces.GameCharAgentProjectType => HandleGameCharAgentProject(client, packet, body),

				// GameCharAgent: the 0x2002/0x2003 collision forms (bind/tutorial) plus every per-character method
				// (loot / shop / respawn / shortcut / latency / ...).
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
