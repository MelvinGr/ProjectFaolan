using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Packets.Conan;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Network.Server.GameServer;
using Faolan.GameServer.Combat;
using Faolan.GameServer.CreateFlow;
using Faolan.GameServer.Dialogue;
using Faolan.GameServer.Economy;
using Faolan.GameServer.Inventory;
using Faolan.GameServer.Quest;
using Faolan.GameServer.Scripting;
using Faolan.GameServer.Session;
using Faolan.GameServer.World;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	public partial class GameServerListener : Server<ConanPacket, ConanPacketFactory, ConanMessageFramer>, IWorldOutbound, IEconomyOutbound, IGameServerProvider
	{
		// Existing-character login no longer emits an opaque post-0x2024 record stream: world entry is CONSTRUCTED
		// from the DB (#210 burst-less entry, SendWorldEntry). The create-editor still has a verbatim burst (#190).
		// Inventory container ID32 types seen in the captured DispatchAddItem inventoryID (these equal the ContainerKind
		// numbers: 0x68 = 104 Bag, 0x71 = 113 Quest). The Casilda-chain captures (casilda_quest_first_stage /
		// compeltefirstquest) deliver the quest key via a live add to 0x71 - the quest-items tab (#261).
		internal const ContainerKind InventoryContainerBackpack = ContainerKind.Bag;
		internal const ContainerKind InventoryContainerQuest = ContainerKind.Quest;

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

		// Default baked-terrain dir (#284) when `TerrainDir` isn't set in faolan.json - the repo-root `Terrain/` folder
		// the importer writes (`--report terrain-export`). The game server reads these small files, NOT the ~28 GB
		// client RDB; if the dir is absent TerrainService disables itself (ground-snap/LoS off, no error).
		private const string DefaultTerrainDir = "Terrain";

		// The Tortage beach world-entry snapshot (0x737A6DF9) embeds quest 10238 in its tails for EVERY character
		// (verified across the Strie / hoxxling / randochar captures - it is the canonical beach starting quest, not
		// a per-character artifact). Since the snapshot is replayed and the client renders that quest, persist it so
		// the DB matches what the journal shows (otherwise CharacterQuests is empty while the journal shows a quest -
		// see snapshot_embeds_strie_quest). See CommitCharacterName / WorldQuest.BeachStarterQuestId.
		private const uint BeachStarterQuestId = WorldQuest.BeachStarterQuestId;

		// The per-application effect nonce a synthesized self-buff carries (the value the old verbatim Scythe-Shield
		// blob used; the client does not validate it - #304).
		private const uint DefaultSelfBuffEffectNonce = 0x14C5C912;

		// Shield of the Risen (#221) base spell ids - the two persistent protection buffs applied on every world entry.
		private const uint ShieldOfTheRisenBase1 = 4061768; // 1h  (wire (base<<8) = 0x3DFA4800)
		private const uint ShieldOfTheRisenBase2 = 3666828; // 10h (wire (base<<8) = 0x37F38C00)

		// Squared world-distance a move must cover to count as a real TRANSLATION that interrupts an in-flight cast
		// (#347). Above pure-rotation jitter (turning in place leaves the position unchanged) but well under one walk
		// step, so any genuine step cancels the cast while aiming a ground-target reticle does not.
		private const float CastMoveCancelEpsilonSq = 0.04f; // (0.2 world units)^2

		// The snapshot record's TypeTag - the 0x0000C350 constant every 0x737A6DF9 carrier carries (= the IIR
		// conversation tag, shared with the 0x201C handshake bodies).
		private const uint SnapshotTypeTag = 0x0000C350;

		// The 0x205A PerformSpecialAction body word that identifies the necro spellweave activate/deactivate toggle
		// (0xFC across all NecroL80 spellweave captures). Other 0x205A actions (0xFF crafting book, combo inputs) are NOT
		// spellweaves.
		private const uint SpellweaveToggleParam = 0xFC;

		// The 0x205A (PerformSpecialAction) selector for the "Path of Asura" teleport spell (NecroL80 path-of-azura
		// capture: PerformSpecialAction 0x4E1 -> retail DispatchRedirectToServer, a long-load teleport). Works like the
		// Totem of Origin - a recall to the bind point (our single-server DoTeleport is the equivalent of retail's
		// cross-server redirect).
		private const uint PathOfAsuraSpecialAction = 0x4E1;

		// Tortage Beach (0xFAA/4010) - the universal fallback world. Its burst is always seeded (the proven blob), so
		// any character whose own map has no usable burst still enters a real, rendered world instead of a hand-built
		// one. (There is no hand-built login/teleport path anymore.)
		//private const uint DefaultWorldMapId = Map.TortageBeachId;

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

		private static readonly byte[] ExistingCharacterPostReadyReportServerIdSender = Endpoints.EndpointBytes(Endpoints.GameCharAgentProjectType, 0x58);

		// The 0x201C handshake uses a different conversation family, but the same sub-id 0x58 + instance tail.
		private static readonly byte[] LoginWorld201CSender = Endpoints.EndpointBytes(Endpoints.GameCharAgentType, 0x58);
		// The create-flow packets are written explicitly (PacketStream.WriteHeader + WriteArray + WriteUInt32) in
		// their Retail*Burst sender methods. The client instance is the character's real id: it is written into the
		// body with WriteUInt32(id) and woven into the sender/receiver conversation id by building each endpoint
		// through Endpoints.EndpointBytes(type, id).

		// The retail login world-entry rides ONE conversation for everything (auth, MapChange, world objects, 		// player and NPC spawns, time): family prefix + sub-id 0x58 on the sender, and the receiver family
		// prefix + the character instance as a varint. Confirmed from the Strie login capture. Keeping every
		// login packet on this single conversation is what lets the client tie the spawns to the loaded
		// playfield and render them (the player avatar renders regardless, but NPCs do not). The bytes live on
		// World.WorldFraming (the single source shared with the WorldBroadcaster); these delegate to it.

		// The player-death-screen packets (death-pose MoveTask, SimpleCharKilledBy, death-state ApplySpell) come from
		// GameAgent at instance 0x59 in the capture (AssassinL23_..._resurect), NOT the 0x58 of WorldFraming.Sender that
		// normal world/NPC-combat broadcasts use. The client appears to gate the player's own dead-state transition on
		// the 0x59 endpoint - sending the full sequence from 0x58 left the avatar alive with the release UI shut (#214).
		private static readonly byte[] PlayerDeathSender = Endpoints.EndpointBytes(Endpoints.GameAgentType, 0x59);

		// Client instances awaiting a TELEPORT/changemap re-entry: added in DoTeleport before the 0x2002 handoff and
		// consumed on the reconnect Hello. A direct GameServer->GameServer changemap reconnect carries NO 0x205F (the
		// retail changemap capture confirms it - the flag is normally armed by the client's 0x205F at full login), so
		// we arm HasPendingPost205FFollowUp ourselves here. Without it the world burst lands but 0x2024 ReadyForPlay-
		// Screen never fires and the client sits on the loading screen forever (the totem-recall hang, Gitea #28).
		// Static for the same reason as PendingCreateWorldEntry: the old + reconnect connections are different sessions
		// on the one listener instance. ConcurrentDictionary-as-set (see PendingCreateWorldEntry) for thread safety.
		private static readonly ConcurrentDictionary<uint, byte> PendingTeleportReentry = new ConcurrentDictionary<uint, byte>();

		private static Dictionary<string, GmCommandDelegate> _gmCommandsDict;

		// Per-application effect-instance id counter for spellweave buffs (so distinct buffs don't collapse onto one
		// client-side effect). Kept in the 0x18xxxxxx neighbourhood of the captured nonces.
		private static int _spellweaveEffectSeq;

		// Personal bank storage (kind 105) delivered on-demand. The withdraw item-grant op is an injected seam.
		// See Economy/BankService.cs.
		private readonly BankService _bank;

		// Combat domain logic: melee/spell damage, NPC kill lifecycle, XP/leveling, kill-quest tracking + the combat
		// wire packets. The opcode dispatch (Handle0x2000) and GM .hurt/.heal call into it. See Combat/CombatService.cs.
		private readonly CombatService _combat;

		// NPC dialogue runtime (TalkToNpc/KnubotAnswer -> captured conversation nodes + quest grants). Composed
		// collaborator; uses the broadcaster for quest pushes + the Lua VM for branch conditions. See Dialogue/ConversationService.cs.
		private readonly ConversationService _conversation;

		// The character-creation -> editor -> destiny-cutscene -> enter-world flow, extracted off this partial into its
		// own collaborator (CreateFlow/CreateFlowService.cs). The inbound create-flow handlers forward to it; the
		// burst-less world entry it needs is injected as a Func seam. SendWorldHandoff stays here (enter-world handoff).
		private readonly CreateFlowService _createFlow;

		// Persists equip/unequip/inventory-move/drop/split actions to the DB (no packet sends). The Inbound
		// handlers call into it. Composed collaborator, like CombatService. See Inventory/EquipmentService.cs.
		private readonly EquipmentService _equipment;

		private readonly IChatServerDispatcher _agentServerDispatcher;
		private readonly IGameServerDispatcher _gameServerDispatcher;

		// Connection keep-alive / latency replies (LatencyCheck 0x2085 -> Pong, Ping 0x2005 -> GameServerStats).
		// See Session/KeepAliveService.cs.
		private readonly KeepAliveService _keepAlive;

		// Loot-window flow (loot-all/single-drag/cash/close): orchestrates the broadcaster's bag state + the
		// item-grant/wallet ports. The loot Inbound cases delegate to it. See World/LootService.cs.
		private readonly LootService _loot;

		// The RDB Lua VM (conversation/quest scripting); stateless + reusable.
		private readonly LuaScriptVm _luaVm = new LuaScriptVm();

		// Player-state persistence (autosave loop + logout/teleport/.save flushes), composed off the listener (#200).
		private readonly PlayerPersistenceService _persistence;

		// The multi-stage quest-chain supersession engine (#261), shared by the kill/loot/talk triggers.
		private readonly QuestProgressionService _questProgression;

		// Scripted Tortage-intro narrative (Kalanthes create-cutscene walk + Casilda beach rescue), #353.
		private readonly QuestHandlerService _questHandler;

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
		private CancellationTokenSource _loopCts;

		public GameServerListener(ILogger logger, IConfiguration configuration, IDatabaseRepository database, Realm realm, IChatServerDispatcher agentServerDispatcher, IGameServerDispatcher gameServerDispatcher)
			: base(realm.Port, logger, configuration, database, new ConanOutgoingPacketLogger(logger, "GameServer", typeof(GameCharInterfaceOpcodes), typeof(GameServer0X2000Opcodes)))
		{
			_agentServerDispatcher = agentServerDispatcher;
			_gameServerDispatcher = gameServerDispatcher;
			// Server-side terrain (ground-snap + line-of-sight, #284) from the importer-baked terrain files - the game
			// server does NOT read the client RDB. Degrades gracefully when the dir is unset/absent.
			var terrain = new TerrainService(configuration["TerrainDir"] ?? DefaultTerrainDir, logger);
			_simulation = new WorldSimulation(this, database, agentServerDispatcher, logger, terrain);
			_createFlow = new CreateFlowService(logger, database, _simulation, SendBurstlessWorldEntry); // create-editor/cutscene/enter-world flow
			_persistence = new PlayerPersistenceService(database, logger, _simulation);
			// Constructed before the broadcaster (needs only the sim + DB) so the broadcaster's interest stream can call
			// into it (Casilda's captive pose) without a construction cycle.
			_questHandler = new QuestHandlerService(database, _simulation, logger);
			_broadcaster = new WorldBroadcaster(_simulation, database, logger, _questHandler);
			WorldBroadcaster.AmbientSpawns = configuration.AmbientSpawnsEnabled; // #363 live ambient spawner (off by default)
			_questProgression = new QuestProgressionService(database, _broadcaster, _questHandler, agentServerDispatcher, logger);
			_combat = new CombatService(_simulation, database, _questProgression, logger, (c, a) => SendAddSpecialAbility(c, a),
				SendLearnSpell); // announced level-up ability + spell unlocks (#209/#73/#74)
			_spellweave = new SpellweaveService(logger, (c, msg) => agentServerDispatcher.SendSystemMessage(c.Session.Account.ClientInstance, msg),
				ApplySpellweaveBuff, // trance + component buffs (correct 54-byte blob)
									 // reactive element list + fire burst (docs/subsystems/combos.md); SendWorldIir wants a [u32 len]-prefixed body, 				// the codec returns the bare record (PetIirCodec convention), so prefix it here.
				(c, record) => SendWorldIir(c, new FaolanStream().WriteArrayUInt32Length(record).ToArray()), SendBuffRemoval, // trance-buff end-twins on cancel/fire
				SendSprintBar, // DispatchUpdateSprintbar (0x200E) - the yellow energy bar
				EnsureEnergyLoop); // start the persistent energy tracking/regen loop
			_conversation = new ConversationService(database, logger, _broadcaster, _questProgression, _luaVm);
			_stockConversation = new StockConversationService(logger, database);
			_loot = new LootService(_broadcaster, this, database, _questProgression, logger);
			_keepAlive = new KeepAliveService();
			_shortcutBar = new ShortcutBarService(database, logger);
			_equipment = new EquipmentService(database, logger);
			_shop = new ShopService(database, logger, this);
			_bank = new BankService(database, logger, this);

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

		public CharacterLiveState GetCharacterLiveState(uint characterId)
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

		public IReadOnlyList<SessionInfo> GetSessions()
		{
			return NetworkClients
				.Select(nc =>
				{
					var account = nc.Session?.Account;
					var character = nc.Session?.Character;
					return new SessionInfo(nc.IpAddress, account?.Id, account?.UserName, account?.ClientInstance ?? 0, character?.Id, character?.Name);
				})
				.ToArray();
		}

		public IReadOnlyList<INetworkClientSession> GetWorldSessions()
		{
			return NetworkClients.Select(nc => nc.Session).Where(s => s != null).ToArray();
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
			if (client.Session.IsCreateEditorConnection)
				return CreateFlowService.FlushPostMorphBurstOnBatchDrained(client);

			return Task.CompletedTask;
		}

		protected override Task ReceivedPacket(INetworkClient client, ConanPacket packet)
		{
			// This connection multiplexes several RPC classes whose 0x20xx method ids COLLIDE with ordinary opcodes, 			// so resolve the RPC CLASS (by receiver; for Tradepost also its sender) BEFORE the opcode switch - each
			// class route consumes the packet. See CLAUDE.md (wire-protocol essentials) + docs/subsystems/tradepost-protocol.md.
			// The two sub-0x2014 opcodes 0x2002/0x2003 are the ONLY ones that also arrive on GameCharAgent (there they
			// are RequestBindLocation / SetTutorialStats); the GameAgent case owns the opcode dispatch for the rest.
			return packet.Receiver.Type switch
			{
				// Tradepost (auction house + mail): inbound packets address the TradepostAgent endpoint.
				Endpoints.TradepostAgentType => HandleTradepostPacket(client, packet),

				// GameCharAgentProject ("post-ready family"): the in-world RequestAggroStatus poll GATES the loading
				// screen (without the reply the client retries forever + never opens chat). Existing-character world login
				// only - the create-editor/create-world flows own this class during their own handshake and must not divert.
				Endpoints.GameCharAgentProjectType => !client.Session.IsCreateEditorConnection && !client.Session.IsCreateWorldSession && client.Session.Character?.Name != null
					? HandleGameCharAgentProject(client, packet)
					: Task.CompletedTask,

				// GameCharAgent: the 0x2002/0x2003 collision forms (bind/tutorial) PLUS every per-character method
				// (loot / shop / respawn / shortcut / latency / ...) - the loopback confirmed these all arrive here.
				Endpoints.GameCharAgentType => HandleGameCharAgentPacket(client, packet),

				// GameAgent (the connection class): auth / chat / ping / server-stats (+ VanityToggle, GCAProject-ish).
				Endpoints.GameAgentType => HandleGameAgentPacket(client, packet),

				_ => throw new Exception($"Unhandled packet receiver type: {packet.Receiver.Type:X8}")
			};
		}

		// The complete burst-less 0x737A6DF9 snapshot packet (DB containers + from-scratch tails).
		// #255: the player's snapshot-eligible held quests + the primary (tracked) quest. The beach starter is the
		// primary (always held + seeded). A held quest joins only if its definition is snapshot-eligible (Quest_c
		// scalars seeded + the 5 header text refs) - else BuildQuestPortion would throw; those fall to the live
		// RestorePlayerQuests path.
		private async Task<(List<QuestDefinition> Quests, QuestDefinition Primary)> LoadSnapshotQuests(Character character)
		{
			var quests = new List<QuestDefinition>();
			if (character == null)
				return (quests, null);

			// Data-driven: the journal is exactly the character's ACTIVE, snapshot-eligible quests - no quest is forced
			// in. A completed/abandoned quest (not Active) is excluded, so it never lingers as a phantom (#...).
			foreach (var cq in (await Database.GetCharacterQuests(character.Id)).Where(q => q.State == QuestState.Active))
			{
				var def = await Database.GetQuestDefinition(cq.QuestId);
				if (FullCharSnapshotSubClass.IsSnapshotEligible(def))
					quests.Add(def);
			}

			// The tracked (primary) quest owns the snapshot's objective region + index terminal: the beach starter when
			// held, else the first held quest (null only when the character holds none -> empty region).
			var primary = quests.FirstOrDefault(q => q.QuestId == BeachStarterQuestId) ?? quests.FirstOrDefault();
			return (quests, primary);
		}

		// Periodically flushes the live world state to the DB so a crash loses at most one interval. Persistence is
		// otherwise event-driven (disconnect, teleport, .save) and needs no game loop; this is just a cheap timer
		// (docs/server-architecture-roadmap.md). The simulation tick, when it lands, can absorb this.
		// The player's live world entity for this connection (the authoritative transform + vitals), or null if not yet
		// in the world. Thin delegate to the simulation, which owns the player registry; kept so the other listener
		// partials (broadcast, combat, GM) read it unchanged.
		private WorldEntity PlayerWorldEntity(INetworkClient client)
		{
			return _simulation.PlayerWorldEntity(client);
		}

		// Resolves a map argument (numeric id, exact case-insensitive name, or first name-contains match) to its Map row, 		// or null if nothing matches. Shared by the `.teleport <name>` and `.teleport <name> x y z` forms (#247).
		private async Task<Map> ResolveMap(string arg)
		{
			if (uint.TryParse(arg, out var id))
			{
				var maps = await Database.GetMaps();
				return maps.FirstOrDefault(m => m.Id == id);
			}

			var all = await Database.GetMaps();
			return all.FirstOrDefault(m => string.Equals(m.Name, arg, StringComparison.OrdinalIgnoreCase)) ??
				   all.FirstOrDefault(m => m.Name.Contains(arg, StringComparison.OrdinalIgnoreCase));
		}

		// .save  - flush the live world state to the DB. The WorldEntity is the source of truth for live transform
		// (docs/server-architecture-roadmap.md) and movement is not otherwise persisted, so this snapshots every
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

	// The world-conversation framing shared by every world-entry / world-event packet: sender = the world-system
	// conversation (family prefix + sub-id 0x58), receiver = the recipient's GameInterfaceType endpoint addressed to its
	// client instance. Confirmed from the Strie login capture - keeping every world packet on this single conversation
	// is what lets the client tie spawns to the loaded playfield and render them. Single source of truth, used by the
	// WorldBroadcaster and by the listener's login/create-flow senders (which delegate here).
	public static class WorldFraming
	{
		// Instance 0x58 from the Strie capture; retail's per-session value varies (hoxxling 0x08, randochar 0x0b).
		// Tested 0x08 (2026-06-13) to clear the client's "n3ToClientQuit sent from 0 -> e_Impossible" warning - it did
		// NOT clear it, so the warning is benign (our despawn body is byte-identical to retail's; it's the client's
		// default IIR PollStatus log). Reverted to 0x58 (the proven value).
		public static readonly byte[] Sender = Endpoints.EndpointBytes(Endpoints.GameAgentType, 0x58);
	}
}
