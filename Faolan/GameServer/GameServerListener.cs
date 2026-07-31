using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Logging;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Network.Server.CsPlayerAgent;
using Faolan.Core.Network.Server.GameServer;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Combat;
using Faolan.GameServer.CreateFlow;
using Faolan.GameServer.Dialogue;
using Faolan.GameServer.Economy;
using Faolan.GameServer.Feats;
using Faolan.GameServer.Inventory;
using Faolan.GameServer.Quest;
using Faolan.GameServer.Session;
using Faolan.GameServer.World;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	public partial class GameServerListener : RpcServer, IWorldOutbound, IEnvironmentOutbound, IEconomyOutbound, ISpellEffectOutbound, ISpellweaveOutbound, ITradeOutbound, ITravelOutbound, IGameServerProvider
	{
		// Squared world-distance a move must cover to count as a real TRANSLATION that interrupts an in-flight cast
		// (#347). Above pure-rotation jitter (turning in place leaves the position unchanged) but well under one walk
		// step, so any genuine step cancels the cast while aiming a ground-target reticle does not.
		private const float CastMoveCancelEpsilonSq = 0.04f; // (0.2 world units)^2

		private const int EnergyRegenPerTick = 600;
		private const int EnergyTickMs = 1000;

		// Server-side ENERGY mirror for sprint: the client owns the yellow bar's visual during sprint, but we track the
		// same drain/regen into session.Energy (silently, no packets) so spellweaving starts from the real value and we
		// know when it hits 0. Rates are approximate (no capture shows the exact sprint drain/regen).
		private const int SprintEnergyDrainPerTick = 900; // ~necro 8910 -> ~10s of continuous sprintource.

		private const uint BankFlagListed = 0x401; // flags value while listed for sale
		private const uint BankFlagMailPending = 0x8001; // flags value while attached to an outgoing mail
		private const uint BankFlagNormal = 0x001; // flags value once unlisted / mail cancelled

		// The beach playfield id MapChange/BuildMapChange emits (0x004FE428); the spawn transform + world objects scope
		// to it. Threaded as one value so the snapshot/spawn/objects agree (per-map sourcing is a follow-up).
		private const uint BeachPlayfieldId = WorldState.DefaultPlayfieldToken;

		// The Tortage beach world-entry snapshot (0x737A6DF9) embeds quest 10238 in its tails for EVERY character
		// (verified across the Strie / hoxxling / randochar captures - it is the canonical beach starting quest, not
		// a per-character artifact). Since the snapshot is replayed and the client renders that quest, persist it so
		// the DB matches what the journal shows (otherwise CharacterQuests is empty while the journal shows a quest -
		// see snapshot_embeds_strie_quest). See CommitCharacterName / QuestContent.BeachStarterQuestId.
		private const uint BeachStarterQuestId = QuestContent.BeachStarterQuestId;

		// The per-application effect nonce a synthesized self-buff carries (the value the old verbatim Scythe-Shield
		// blob used; the client does not validate it - #304).
		private const uint DefaultSelfBuffEffectNonce = 0x14C5C912;

		// The 0x205A (PerformSpecialAction) selector for the "Path of Asura" teleport spell (NecroL80 path-of-azura
		// capture: PerformSpecialAction 0x4E1 -> retail DispatchRedirectToServer, a long-load teleport). It recalls to
		// the BOUND location (our single-server DoTeleport is the equivalent of retail's cross-server redirect).
		// ⚠️ Do NOT conflate it with the Totem of Origins: the Totem returns to the RACIAL home city instead
		// (StartingData.RacialHomeMap). Two different destinations, two different handlers.
		private const uint PathOfAsuraSpecialAction = 0x4E1;

		// Shield of the Risen (#221) base spell ids - the two persistent protection buffs applied on every world entry.
		private const uint ShieldOfTheRisenBase1 = 4061768; // 1h  (wire (base<<8) = 0x3DFA4800)
		private const uint ShieldOfTheRisenBase2 = 3666828; // 10h (wire (base<<8) = 0x37F38C00)

		// (The spellweave activate/deactivate selector used to live here as a single 0xFC constant. It is PER CLASS -
		// necromancer 0xFC, demonologist 0x10C - so it moved to StartingData.SpellweaveToggleAction, which carries the
		// corpus evidence for each row.)
		// The create-flow packets build each endpoint through Endpoints.Endpoint(type, id) with the client instance
		// (the character's real id) woven into the sender/receiver conversation id.

		// The world-conversation sender shared by every world-entry / world-event packet (formerly the WorldFraming
		// class): sender = the world-system conversation (family prefix hash("GameAgent") + sub-id 0x58); the receiver
		// is the recipient's GameInterface endpoint addressed to its client instance. Confirmed from the Strie login
		// capture - keeping every world packet on this single conversation is what lets the client tie the spawns to the
		// loaded playfield and render them. Public because the composed collaborators (WorldBroadcaster, CombatService,
		// the economy services, VitalsPacket) frame their own world sends against it.
		// Instance 0x58 from the Strie capture; retail's per-session value varies (hoxxling 0x08, randochar 0x0b).
		// Tested 0x08 (2026-06-13) to clear the client's "n3ToClientQuit sent from 0 -> e_Impossible" warning - it did
		// NOT clear it, so the warning is benign (our despawn body is byte-identical to retail's). Reverted to 0x58.
		public static readonly ProtoEndpoint WorldSenderEndpoint = Endpoints.Endpoint(Endpoints.GameAgentType, 0x58);

		// (Tortage Beach, Map.TortageBeachId, is the universal fallback world - its burst is always seeded, so a
		// character whose own map has no usable burst still enters a real rendered world. The world-entry path reads
		// it directly; there is no hand-built login/teleport path anymore, so no constant here selects it.)

		// Starter beach gear (StartingData.StartingMaleLegsItem / StartingWristItem / …) lives in
		// Faolan.Core.Network/Constants/StartingData.cs.

		// Equipped-container slot ids = the CharacterEquipmentSlot enum itself, read through the CharacterEquipment
		// indexer (the same source PlayerSpawnEquipment.BuildInventoryEntries walks for the spawn). This was a
		// hand-maintained parallel table of the 18 WORN slots, which silently dropped the 6 VANITY slots (1002+) out of
		// the snapshot's equipped container: the avatar rendered its vanity off the FA015807 spawn, but the client's
		// equipment/vanity panel came up empty because the container the panel reads never carried them. Retail DOES
		// ship vanity here - NecroL80's L80 snapshot's equipped container holds pos 1002/1007/1011/1014/1018 alongside the
		// worn set. FirstPosBag (17) is NOT an enum member and stays handled separately (bagFirst).
		private static readonly CharacterEquipmentSlot[] InventoryEquippedSlots = Enum.GetValues<CharacterEquipmentSlot>();

		// Client instances awaiting a TELEPORT/changemap re-entry: added in DoTeleport before the 0x2002 handoff and
		// consumed on the reconnect Hello. A direct GameServer->GameServer changemap reconnect carries NO 0x205F (the
		// retail changemap capture confirms it - the flag is normally armed by the client's 0x205F at full login), so
		// we arm HasPendingPost205FFollowUp ourselves here. Without it the world burst lands but 0x2024 ReadyForPlay-
		// Screen never fires and the client sits on the loading screen forever (the totem-recall hang, Gitea #28).
		// Static for the same reason as PendingCreateWorldEntry: the old + reconnect connections are different sessions
		// on the one listener instance. ConcurrentDictionary-as-set (see PendingCreateWorldEntry) for thread safety.
		private static readonly ConcurrentDictionary<uint, byte> PendingTeleportReentry = new ConcurrentDictionary<uint, byte>();

		private static Dictionary<string, GmCommandDelegate> _gmCommandsDict = null!;

		private static readonly ProtoEndpoint ExistingCharacterPostReadyReportServerIdSenderEndpoint = Endpoints.Endpoint(Endpoints.GameCharAgentProjectType, 0x58);

		// The 0x201C handshake uses a different conversation family, but the same sub-id 0x58 + instance tail.
		private static readonly ProtoEndpoint LoginWorld201CSenderEndpoint = Endpoints.Endpoint(Endpoints.GameCharAgentType, 0x58);

		// The player-death-screen packets (death-pose MoveTask, SimpleCharKilledBy, death-state ApplySpell) come from
		// GameAgent at instance 0x59 in the capture (AssassinL23_..._resurect), NOT the 0x58 of WorldSenderEndpoint that
		// normal world/NPC-combat broadcasts use. The client appears to gate the player's own dead-state transition on
		// the 0x59 endpoint - sending the full sequence from 0x58 left the avatar alive with the release UI shut (#214).
		private static readonly ProtoEndpoint PlayerDeathSender = Endpoints.Endpoint(Endpoints.GameAgentType, 0x59);

		// Per-application effect-instance id counter for spellweave buffs (so distinct buffs don't collapse onto one
		// client-side effect). Kept in the 0x18xxxxxx neighbourhood of the captured nonces.
		private static int _spellweaveEffectSeq;

		// Phase shift for the world clock, in game seconds (#351). The clock itself runs off the wall clock
		// (GameTimeCodec); this is what `.time` moves so a tester can jump to dusk without waiting out a 4.8h game day.
		private long _gameTimeOffset;

		// Personal bank storage (kind 105) delivered on-demand. The withdraw item-grant op is an injected seam.
		// See Economy/BankService.cs.
		private readonly BankService _bank;

		// #367: what an active buff adds to its carrier's stats (armor/ratings/attributes), from the imported RDB
		// [2][9] stat effects. Recomputes `gear + buffs` whenever a buff lands or ends; see Combat/BuffStatEffects.cs.
		private readonly BuffStatEffects _buffStats;
		private readonly ClaimService _claims;

		// Combat domain logic: melee/spell damage, NPC kill lifecycle, XP/leveling, kill-quest tracking + the combat
		// wire packets. The opcode dispatch (Handle0x2000) and GM .hurt/.heal call into it. See Combat/CombatService.cs.
		private readonly CombatService _combat;

		// NPC dialogue runtime (TalkToNpc/KnubotAnswer -> captured conversation nodes + quest grants). Composed
		// collaborator; uses the broadcaster for quest pushes + C# ConditionQuestId/State gates for branch conditions. See Dialogue/ConversationService.cs.
		private readonly ConversationService _conversation;

		// #523: how close the player must come to an NPC for its APPROACH-triggered conversation to open. MEASURED at
		// 12.9 / 13.8 / 10.9 units across three independent captures of Saddur's beach confrontation.
		private const float ApproachConversationRadius = 15f;

		// The NPC names that HAVE an approach-triggered conversation, cached on first use - a small static set, so the
		// per-move check costs one hash lookup for the overwhelming majority of players who are near nobody special.
		private HashSet<string>? _approachConversationNpcs;
		private readonly FatalityService _fatality;

		// #439: the spell effects the server acts on itself - pet summons and triggered procs, from the SpellEffect
		// table. See Combat/SpellEffectService.cs.
		private readonly SpellEffectService _spellEffects;

		// The character-creation -> editor -> destiny-cutscene -> enter-world flow, extracted off this partial into its
		// own collaborator (CreateFlow/CreateFlowService.cs). The inbound create-flow handlers forward to it; the
		// burst-less world entry it needs is injected as a Func seam. SendWorldHandoff stays here (enter-world handoff).
		private readonly CreateFlowService _createFlow;

		// Persists equip/unequip/inventory-move/drop/split actions to the DB (no packet sends). The Inbound
		// handlers call into it. Composed collaborator, like CombatService. See Inventory/EquipmentService.cs.
		private readonly EquipmentService _equipment;

		// Composed collaborator: non-combat damage - fall damage + the scripted hazard volumes (#495/#504). See
		// World/EnvironmentService.cs.
		private readonly EnvironmentService _environment;

		// Composed collaborator: knockback - the server shoving an entity along a segment it did not walk (#496). See
		// World/KnockbackService.cs.
		private readonly KnockbackService _knockback;

		// Composed collaborator: local travel - the NPCs who move a player for a fee (#506). See World/TravelService.cs.
		private readonly TravelService _travel;
		private readonly FeatService _feats;

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
		private readonly QuestHandlerService _questHandler;

		// The multi-stage quest-chain supersession engine (#261), shared by the kill/loot/talk triggers.
		private readonly QuestProgressionService _questProgression;

		// NPC vendor / shop sell-buy-undo flow (owns the open-shop/last-sale state). The wallet-update + item-grant
		// outbound ops are injected as seams (shared with other subsystems). See Economy/ShopService.cs.
		private readonly ShopService _shop;

		// The player action bar / hotbar persistence + login restore (#71). See Inventory/ShortcutBarService.cs.
		private readonly ShortcutBarService _shortcutBar;

		private readonly SpellweaveService _spellweave;

		// The cutscene stock-phrase subtitle channel (Kalanthes's beach lines): answers PlayStockphraseAsSubtitle
		// (0x205E) with the captured remoteformat, distinct from the interactive _conversation. See Dialogue/StockConversationService.cs.
		private readonly StockConversationService _stockConversation;

		// Builds + sends the world-event packets (spawns/moves/despawns, NPC respawn/loot/mount renders, loot-window
		// and quest-journal packets). Reads the world model via the simulation; the listener delegates its IWorldOutbound
		// port + the loot/quest opcode handlers to it. See World/WorldBroadcaster.cs.
		private readonly WorldBroadcaster _broadcaster;

		// The live world simulation: owns the world model, the connected players' authoritative entities, and the
		// 10 Hz tick (NPC respawn/AI, vitals regen, player death/respawn, resurrection discovery). The listener is its
		// IWorldOutbound port. Built in the ctor (after the dispatchers are assigned). See World/WorldSimulation.cs.
		private readonly WorldSimulation _simulation;

		// Background loops (persistence + simulation each own their own loop) and their shared lifetime token. Held
		// (not fire-and-forget) so StopAsync can signal them and await a clean shutdown, and so any unobserved fault
		// surfaces instead of being swallowed.
		private CancellationTokenSource _loopCts = null!;

		public GameServerListener(ILogger logger, IConfiguration configuration, IDatabaseRepository database, Realm realm, IChatServerDispatcher agentServerDispatcher,
			IGameServerDispatcher gameServerDispatcher, ICsPlayerAgentDispatcher csPlayerAgentDispatcher)
			: base(realm.Port, logger, configuration, database, new GameServerPacketTracer(logger))
		{
			_agentServerDispatcher = agentServerDispatcher;
			_gameServerDispatcher = gameServerDispatcher;
			// The team roster lives on the CS tier; LeaveTeam/KickFromTeam arrive HERE, so they are routed over there.
			_csPlayerAgentDispatcher = csPlayerAgentDispatcher;
			// Server-side terrain (ground-snap + line-of-sight, #284) from the importer-baked terrain files - the game
			// server does NOT read the client RDB. Degrades gracefully when the dir is unset/absent.
			var terrain = new TerrainService(configuration["TerrainDir"], logger);
			_simulation = new WorldSimulation(this, database, agentServerDispatcher, logger, terrain);
			// create-editor/cutscene/enter-world flow. The handoff seam washes the new character ashore before the
			// redirect (#234: changemap-prep IntendedTeleport 0x10D27BC0 + n3Teleport 0x5A32F0D7 to the Tortage Beach
			// spawn - Blablabla create capture f444/449, dest map 0x0FAA, pos 322.5,150,704.9, descriptor 0x32 - else
			// the player stays at the drowning cutscene's underwater end position), then hands off like DoTeleport.
			_createFlow = new CreateFlowService(logger, database, _simulation, SendBurstlessWorldEntry, async client =>
			{
				await SendChangeMapPrep(client, Map.TortageBeachId, Map.NewPlayerSpawnSpawnPoint.Position, 0x32);
				await SendWorldHandoff(client);
			});
			_persistence = new PlayerPersistenceService(database, logger, _simulation);
			_trade = new TradeService(database, logger, this); // player<->player trade (#101); reaches the wire via ITradeOutbound
			// Constructed before the broadcaster (needs only the sim + DB) so the broadcaster's interest stream can call
			// into it (Casilda's captive pose) without a construction cycle.
			_questHandler = new QuestHandlerService(database, _simulation, logger);
			_broadcaster = new WorldBroadcaster(_simulation, database, logger, _questHandler);
			_questProgression = new QuestProgressionService(database, _broadcaster, _questHandler, agentServerDispatcher, logger);
			// #439: what a spell makes the SERVER do (summon a pet, arm a proc) - dispatched from the SpellEffect rows
			// instead of from spell ids hardcoded into the cast path. Reaches the wire through ISpellEffectOutbound,
			// which this listener implements (GameServerListener.Buffs.cs).
			_spellEffects = new SpellEffectService(database, this, logger);
			// #140: the fatality burst. The buffs that PROC on a fatality are SpellEffect rows now, so this owns only
			// the roll + the two Fatality ids every captured burst carries.
			_fatality = new FatalityService(logger, this, _spellEffects);
			_buffStats = new BuffStatEffects(database, logger);
			_combat = new CombatService(_simulation, database, _questProgression, logger, (c, a) => SendAddSpecialAbility(c, a),
				SendLearnSpell, _fatality); // announced level-up ability + spell unlocks (#209/#73/#74), fatality burst (#140)
			// Seven Func<> seams collapsed into ISpellweaveOutbound, which this listener implements
			// (GameServerListener.Buffs.cs) - see the port for why.
			_spellweave = new SpellweaveService(logger, this);
			_conversation = new ConversationService(database, logger, _broadcaster, _questProgression);
			_stockConversation = new StockConversationService(logger, database);
			_loot = new LootService(_broadcaster, this, database, _questProgression, agentServerDispatcher, logger);
			_keepAlive = new KeepAliveService(_simulation.Metrics);
			_shortcutBar = new ShortcutBarService(database, logger);
			_equipment = new EquipmentService(database, logger);
			_shop = new ShopService(database, logger, this);
			_bank = new BankService(database, logger, this);
			_claims = new ClaimService(database, logger, this, agentServerDispatcher);
			_feats = new FeatService(database, logger, agentServerDispatcher, DoTeleport);
			// #495/#504: "the world damaged you" - fall damage and the scripted hazard volumes. Driven from the move
			// handler (both triggers are edges on the player's own position stream), reaching the wire through
			// IEnvironmentOutbound, which this listener implements (GameServerListener.Combat.cs).
			_environment = new EnvironmentService(database, this, logger);
			// #506: local travel. Reaches the wire through ITravelOutbound (GameServerListener.Travel.cs).
			_travel = new TravelService(database, this, logger);
			// #496: knockback. The buff apply is a single shared op, so it comes in as a delegate seam rather than a port.
			_knockback = new KnockbackService(_simulation, _broadcaster, (c, id) => ((ISpellEffectOutbound)this).ApplySelfBuff(c, id), logger);

			InitGmCommands();
		}

		public bool DisconnectCharacter(uint characterId)
		{
			var client = ClientForCharacter(characterId);
			if (client == null)
				return false;

			client.Stop();
			return true;
		}

		public CharacterLiveState? GetCharacterLiveState(uint characterId)
		{
			var client = ClientForCharacter(characterId);
			var entity = client == null
				? null
				: _simulation.PlayerWorldEntity(client);
			if (entity == null)
				return null;

			var buffs = entity.ActiveBuffs.Keys.OrderBy(id => id).Select(id => $"0x{id:X8}").ToArray();
			return new CharacterLiveState(characterId, entity.Name, entity.Handle, entity.MapId, entity.Position.X, entity.Position.Y, entity.Position.Z, entity.Vitals?.Health,
				entity.Vitals?.MaxHealth, buffs);
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

		public async Task<bool> RunGmCommand(uint characterId, string commandText)
		{
			var client = ClientForCharacter(characterId);
			if (client == null)
				return false;

			// HandleGmCommand strips a single leading prefix char (".teleport" -> "teleport"), so ensure one is present.
			if (commandText.Length == 0 || char.IsLetterOrDigit(commandText[0]))
				commandText = "." + commandText;

			await HandleGmCommand(client, commandText);
			return true;
		}

		public override Task StartAsync(CancellationToken cancellationToken)
		{
			_gameServerDispatcher.Register(this);
			// Own lifetime token (linked to startup) so StopAsync can cancel the loops independently of any single
			// connection. Tasks are stored so they can be awaited on shutdown.
			_loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			_persistence.Start(_loopCts.Token); // periodic crash-safety flush of the live world state
			_simulation.Start(_loopCts.Token); // the simulation tick (regen, NPC AI, death/respawn, discovery)
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

			// A trade cannot outlive either connection. Nothing has moved at this point - the exchange only happens on the
			// double accept - so there is nothing to roll back, just state to drop.
			if (client.Session.Character is { } tradingCharacter)
				_trade.Abandon(tradingCharacter.Id);

			// A logout also leaves the team - otherwise the roster keeps a member nobody can reach, and the next join
			// broadcast pushes at a dead connection. Retail dissolves a one-member team, which TeamService does too.
			if (client.Session.Character is { } character)
				await _csPlayerAgentDispatcher.LeaveTeam(character.Id);

			await base.ClientDisconnected(client);
		}

		// GameCharAgent.RequestBindLocation (0x2002 on the GameCharAgent class): the client repeatedly polls its
		// bind/home playfield (the Totem of Origin destination, #64); body = the player's ID32. Retail answers each
		// with a 4-byte 0x2002 [u32 playfieldId] (verified 1:1 across 13 captures). Routed here by receiver class so
		// it never collides with GameAgent.ChatVicinity, which shares opcode 0x2002.
		// #197: after each recv batch drains, fire the create-editor post-morph trio deterministically (when the
		// batch that carried Finish + the SetMorphValue burst is fully received) instead of the debounce timer.
		// Guarded to the create-editor customization phase; a no-op for every other connection/batch.
		protected override Task ReceivedBatchComplete(INetworkClient client)
		{
			if (client.Session.EntryMode == WorldEntryMode.CreateEditor)
				return CreateFlowService.FlushPostMorphBurstOnBatchDrained(client);

			return Task.CompletedTask;
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
			// read a sub-header first (Handle0X2000) leave the cursor advanced for their own sub-handlers.
			var body = packet.BodyReader();

			return packet.Receiver.Type switch
			{
				// Tradepost (auction house + mail): inbound packets address the TradepostAgent endpoint.
				Endpoints.TradepostAgentType => HandleTradepostPacket(client, packet, body),

				// GameCharAgentProject ("post-ready family"): the in-world RequestAggroStatus poll GATES the loading
				// screen (without the reply the client retries forever + never opens chat). Existing-character world login
				// only - the create-editor/create-world flows own this class during their own handshake and must not divert.
				Endpoints.GameCharAgentProjectType => client.Session is { EntryMode: WorldEntryMode.Login or WorldEntryMode.TeleportReentry, Character.Name: not null }
					? HandleGameCharAgentProject(client, packet, body)
					: Task.CompletedTask,

				// GameCharAgent: the 0x2002/0x2003 collision forms (bind/tutorial) PLUS every per-character method
				// (loot / shop / respawn / shortcut / latency / ...) - the loopback confirmed these all arrive here.
				Endpoints.GameCharAgentType => HandleGameCharAgentPacket(client, packet, body),

				// GameAgent (the connection class): auth / chat / ping / server-stats (+ VanityToggle, GCAProject-ish).
				Endpoints.GameAgentType => HandleGameAgentPacket(client, packet, body),

				_ => throw new Exception($"Unhandled packet receiver type: {packet.Receiver.Type:X8}")
			};
		}

		// The area rows + area quest catalogs the quest INDEX needs for a set of active quests. The catalog is every
		// quest of the area ordered by JournalSequence - static area data, not the character's journal: a freshly
		// created character carries its area's whole list in the captures (#390).
		private async Task<(Dictionary<uint, QuestArea> Areas, Dictionary<uint, IReadOnlyList<QuestDefinition>> Catalog)> LoadAreaCatalog(IReadOnlyList<QuestDefinition> quests)
		{
			var areas = new Dictionary<uint, QuestArea>();
			var catalog = new Dictionary<uint, IReadOnlyList<QuestDefinition>>();
			if (quests.Count == 0)
				return (areas, catalog);

			var allAreas = (await Database.GetQuestAreas()).ToDictionary(a => a.AreaId);
			foreach (var areaId in quests.Select(q => q.AreaId).Distinct())
			{
				if (allAreas.TryGetValue(areaId, out var area))
					areas[areaId] = area;

				catalog[areaId] = await Database.GetQuestDefinitionsByArea(areaId);
			}

			return (areas, catalog);
		}

		// The character's COMPLETED-quest journal, grouped by area (#390) - the Completed tab's contents. Retail ships
		// this in the login snapshot and sends no quest packets at all before the first in-session accept, so this is
		// the ONLY path that populates that tab.
		//
		// The order is ours to choose: the wire order is per-character in the captures, but the client re-sorts for
		// display (retail renders alphabetically while the wire does not), so a stable areaId/questId ordering is
		// enough. A quest whose area has no row, or that has no definition, is skipped rather than guessed at.
		private async Task<List<(QuestArea Area, IReadOnlyList<QuestDefinition> Quests)>> LoadCompletedJournal(Character character)
		{
			var groups = new List<(QuestArea, IReadOnlyList<QuestDefinition>)>();

			var completed = (await Database.GetCharacterQuests(character.Id)).Where(q => q.State == QuestState.Completed).ToList();
			if (completed.Count == 0)
				return groups;

			var areas = (await Database.GetQuestAreas()).ToDictionary(a => a.AreaId);
			var byArea = new SortedDictionary<uint, List<QuestDefinition>>();

			foreach (var cq in completed)
			{
				var def = await Database.GetQuestDefinition(cq.QuestId);
				if (def == null || def.AreaId == 0 || !areas.ContainsKey(def.AreaId))
					continue;

				(byArea.TryGetValue(def.AreaId, out var list)
					? list
					: byArea[def.AreaId] = []).Add(def);
			}

			foreach (var (areaId, defs) in byArea)
				groups.Add((areas[areaId], defs.OrderBy(d => d.QuestId).ToList()));

			return groups;
		}

		// The complete burst-less 0x737A6DF9 snapshot packet (DB containers + from-scratch tails).
		// #255: the player's snapshot-eligible held quests + the primary (tracked) quest. The beach starter is the
		// primary (always held + seeded). A held quest joins only if its definition is snapshot-eligible (Quest_c
		// scalars seeded + the 5 header text refs) - else BuildQuestPortion would throw. An ineligible held quest is
		// simply ABSENT from the journal: there is no live restore path any more (#389/#390), so widening
		// IsSnapshotEligible is what makes such a quest appear.
		private async Task<(List<QuestDefinition> Quests, QuestDefinition? Primary)> LoadSnapshotQuests(Character character)
		{
			var quests = new List<QuestDefinition>();

			// Data-driven: the journal is exactly the character's ACTIVE, snapshot-eligible quests - no quest is forced
			// in. A completed/abandoned quest (not Active) is excluded, so it never lingers as a phantom (#...).
			var areaRows = (await Database.GetQuestAreas()).ToDictionary(a => a.AreaId);
			foreach (var cq in (await Database.GetCharacterQuests(character.Id)).Where(q => q.State == QuestState.Active))
			{
				var def = await Database.GetQuestDefinition(cq.QuestId);
				if (def != null && areaRows.TryGetValue(def.AreaId, out var area) && FullCharSnapshotSubClass.IsSnapshotEligible(def, area))
					quests.Add(def);
			}

			// The tracked (primary) quest owns the snapshot's objective region + index terminal: the beach starter when
			// held, else the first held quest (null only when the character holds none -> empty region).
			var primary = quests.FirstOrDefault(q => q.QuestId == BeachStarterQuestId) ?? quests.FirstOrDefault();
			return (quests, primary);
		}

		// Periodically flushes the live world state to the DB so a crash loses at most one interval. Persistence is
		// otherwise event-driven (disconnect, teleport, .save) and needs no game loop; this is just a cheap timer
		// (Gitea #404). The simulation tick, when it lands, can absorb this.
		// The player's live world entity for this connection (the authoritative transform + vitals), or null if not yet
		// in the world. Thin delegate to the simulation, which owns the player registry; kept so the other listener
		// partials (broadcast, combat, GM) read it unchanged.
		private WorldEntity? PlayerWorldEntity(INetworkClient client)
		{
			return _simulation.PlayerWorldEntity(client);
		}

		// Resolves a map argument (numeric id, exact case-insensitive name, or first name-contains match) to its Map row, 		// or null if nothing matches. Shared by the `.teleport <name>` and `.teleport <name> x y z` forms (#247).
		private async Task<Map?> ResolveMap(string arg)
		{
			if (uint.TryParse(arg, out var id))
			{
				var maps = await Database.GetMaps();
				return maps.FirstOrDefault(m => m.Id == id);
			}

			var all = await Database.GetMaps();
			return all.FirstOrDefault(m => string.Equals(m.Name, arg, StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault(m => m.Name.Contains(arg, StringComparison.OrdinalIgnoreCase));
		}

		// .save  - flush the live world state to the DB. The WorldEntity is the source of truth for live transform
		// (Gitea #404) and movement is not otherwise persisted, so this snapshots every
		// connected player's authoritative position/map back onto their persisted Character and saves it. (Extends to
		// more live state - vitals, buffs, … - as those become persisted.)
		private async Task SaveChanges(uint characterId)
		{
			var saved = await _persistence.FlushAllAsync();
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Flushed {saved} player(s)' live world state to the database.");
		}

		// Parses a wire handle from a GM-command argument: "0x"-prefixed hex or plain decimal. Returns false on a
		// malformed/overflowing value.
		private static bool TryParseHandle(string arg, out uint handle)
		{
			handle = 0;
			try
			{
				handle = arg.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
					? Convert.ToUInt32(arg[2..], 16)
					: uint.Parse(arg);
				return true;
			}
			catch (FormatException)
			{
				return false;
			}
			catch (OverflowException)
			{
				return false;
			}
		}
	}
}
