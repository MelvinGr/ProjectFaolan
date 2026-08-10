using Faolan.Core.Constants;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Progression;
using Faolan.GameServer.Combat;
using Faolan.GameServer.Quest;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	// Turns world-model events into wire packets: spawns/moves/despawns for interest management, NPC respawn/loot/mount
	// renders, loot-window and quest-journal packets (Gitea #404). Extracted out of the
	// GameServer listener so packet CONSTRUCTION for world events is its own collaborator; it reads the world model via
	// the WorldSimulation, loads templates from the DB, and frames every packet on the world conversation (WorldSenderEndpoint:
	// sender = world-system 0x58, receiver = the recipient's GameInterfaceType endpoint). The FA015807 body carries the
	// SUBJECT's handle (unchanged), so the recipient renders the subject as a separate entity. NEEDS IN-CLIENT
	// VERIFICATION with two connected players. Single-player is a no-op (no interest events).
	public sealed class WorldBroadcaster : IQuestOutbound
	{
		// DispatchSetQuestMarker (0x2008) state byte (decoded #241): 0x02 = '!' (quest available), 0x04 = '?' (ready
		// to turn in), 0x00 = none. 0x01 = the SILVER '!' - a quest NPC with nothing for you right now (#559).
		//
		// The silver state was missed by #241, which only saw three values. It is the second most common on the wire:
		// of the 301 replies in xxx_mage-necromancer_login_play2, 181 are 0x00, 74 are 0x01, 30 are 0x02, 16 are 0x04.
		// Turach and Laranga mirror each other across that capture and pin what it means - Turach is 0x01 at login
		// (his quest needs Laranga first), 0x04 once she points at him, 0x01 again while his own quest is in progress,
		// 0x04 when the bricks are collected, 0x01 after the hand-in. So 0x01 covers BOTH "prerequisite not met" and
		// "held, or already turned in - nothing more here".
		//
		// ⚠️ 0x00 and 0x01 are NOT interchangeable: 52 NPCs in that capture only ever returned 0x00, and they are the
		// ones that give no quest at all. Silver says "this NPC is part of a chain"; none says "this NPC is scenery".
		private const byte QuestMarkerNone = 0x00, QuestMarkerUnavailable = 0x01, QuestMarkerAvailable = 0x02, QuestMarkerTurnIn = 0x04;

		// How long a dropped loot bag lingers before the tick fades + removes it (the IsLoot expiry).
		private const double LootBagExpirySeconds = 120.0;
		private const double PatrolDwellSeconds = 2.0;

		// #703: how close a route's last waypoint must come back to its first for the route to count as CLOSED (walked
		// round and round) rather than open (walked back down again). 494 of the 1,318 stored routes close this way.
		private const float RouteClosedTolerance = 5f;

		// How far a loot bag may be ground-snapped from the NPC's death Y. The snap fixes death-moment Y drift (tall /
		// flying creatures, MoveTask Z-drift #190) - a few metres. Beyond that the heightmap is describing the OUTDOOR
		// SURFACE above an interior/cave kill, not the floor the creature stood on, so the snap is refused and the
		// death Y wins. Outdoor kills sit ~1m off; the cave kill that exposed this was 28m.
		private const float MaxLootGroundSnap = 5f;

		// (#266's PatrolWalkSpeed estimate lived here. It is now WorldState.RouteWalkSpeed, because the same number has
		// to time the re-issue cadence AND advance the model, and having two copies is how they drifted - the comment
		// here still claimed "NpcMoveSpeed (12) is the RUN speed" long after NpcMoveSpeed became 7. It is also no longer
		// an estimate: it is measured off retail's own patrollers, see #714.)

		// How far a seated player must move from their seat before they stand up. While seated the client keeps
		// streaming position syncs at (roughly) the seat position - plus a one-time seat-snap of up to ~1 unit - and
		// those must NOT be read as "left the bench" (that stood the player straight back up). Only a real walk exceeds
		// this. Capture: the seated position syncs drift < 1 unit; the player is well past this by the time standup fires.
		// How far from the SEAT the player must get before the server stands them up. Retail's sit capture puts it at
		// ~1.25: the seated player is parked at Z=620.49 (pkt 6812), wobbles 0.10-0.19 while sitting, then reaches
		// Z=621.74 and the standup goes out immediately (pkt 6841 -> 6844). 2.0 was too generous to ever fire - in
		// the in-client bench test the walk-off only reached 1.78 from the seat and the server never stood the player up.
		private const float SeatLeaveDistance = 1.0f;

		// The object class the sit remoteformat names (capture: startobjectaction(33)|turnon_execute(4)|TavernBenchSimpleDynel).
		private const string BenchObjectClass = "TavernBenchSimpleDynel";

		// LootBagFullUpdateIIR_c - the world-object sub-opcode a loot bag spawns as.

		// The combat-mode stat value the captured beach melee mobs send at aggro (strickenbla capture, #218): the tick
		// before each mob's first swing it pushes `CombatMode = 4`, which is what draws its weapon. (The player uses 1
		// and one non-swinging entity used 1 - the field is a stance/mode code that isn't fully decoded, but 4 is the
		// value every actual melee swinger sends.)
		private const uint NpcCombatModeValue = 4;

		// Dev visualization (#363): when true, an NPC that resolves to an RDB spawn AREA has that area's label pushed as
		// its overhead nameplate (via SetNameIIR after spawn) so a manual creature-labeling pass sees which area it's in.
		// Off by default; toggled live by the `.togglenpcgroup` GM command (ToggleAreaLabels) - no config.
		//
		// INSTANCE state, not static. It is a per-world-host toggle and this class is per-host, so a static made it
		// process-global - which under `--role login --role realm=<id>` (or the all-in-one dev run) would mean one
		// GM's debug nameplates following the other realm's players around. Nothing outside this class touches it.
		private bool _debugAreaLabels;

		// #363 ambient spawn-group pools per loaded zone (built in LoadAmbientSpawns on the entry thread, iterated by
		// MaintainAmbientPools on the tick thread - concurrent so the tick doesn't throw mid-load).
		private readonly ConcurrentDictionary<(uint MapId, uint InstanceId), List<AmbientPool>> _ambientPools = new ConcurrentDictionary<(uint MapId, uint InstanceId), List<AmbientPool>>();

		// (mapId, instanceId) zones whose NPCs have been loaded into the world model (once per instance, on first entry
		// to that copy - each instance gets its own NPC population) (#268).
		private readonly ConcurrentDictionary<(uint MapId, uint InstanceId), byte> _npcLoadedZones = new ConcurrentDictionary<(uint MapId, uint InstanceId), byte>();
		private readonly ConcurrentDictionary<(uint MapId, uint InstanceId), byte> _objectLoadedZones = new ConcurrentDictionary<(uint MapId, uint InstanceId), byte>();

		// Cached loot-bag template (a captured "Bag of loot" world object) per map; null values cache loot-less maps.
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;

		// _questHandler is constructed before the broadcaster (it needs only the sim + DB), so the interest stream can
		// call into it (Casilda's captive pose on stream-in) without a construction cycle.
		private readonly ComboService _combos;
		private readonly QuestHandlerService _questHandler;

		private readonly WorldSimulation _sim;

		// Quest-giver NPC name -> granted quest id, cached once (small set).
		private Dictionary<uint, uint> _questGivers = null!;

		// Quest-gated NPC TEMPLATE id -> the quest ids that reveal it (#261, re-keyed off the name in #521). Cached once;
		// an NPC in this map is streamed only to players holding one of its quests active (see WorldEntity.GateQuestIds
		// / IsVisibleTo).
		private Dictionary<uint, uint[]> _questGatedNpcs = null!;
		private Dictionary<uint, uint> _escortHiddenNpcs = null!;

		// Monotonic allocator for ALL runtime-spawned entity handles - dropped-loot bags, pets, summoned NPCs,
		// and the #363 ambient pool members (not loot-specific despite the range's origin). CRITICAL: the
		// value must be in the SAME LOW range retail assigns mid-session entity handles - the client only tracks/polls
		// handles in that range, and the render of a mid-session spawn is completed by the client polling 0x2020 for the
		// entity's handle (-> server SpawnCheck 0x2008). Captured retail mid-session spawns (the mount + interest NPCs)
		// use 0x000286xx; the burst NPCs use 0x000818xx; the player/char-id is ~0x0802xxxx. A high value (the old
		// 0x40000000, or even 0x0F000000) is FAR above any real handle, so the client never polls it and the entity
		// silently never renders. 0x00090000 sits just above the burst NPCs (0x000818xx) and well below the char-id
		// range (0x02000000+) - small, valid, collision-free. (Matches the RetailLoginReplay built-NPC test handle.)
		private int _nextRuntimeHandle = 0x00090000;

		public WorldBroadcaster(WorldSimulation sim, IDatabaseRepository database, ILogger logger, QuestHandlerService questHandler, ComboService combos)
		{
			_sim = sim;
			_database = database;
			_logger = logger;
			_questHandler = questHandler;
			_combos = combos;
		}

		// #262: apply the AFTER-QUEST world-object effects for this player - every object on their map whose EffectQuestId
		// they hold ACTIVE or COMPLETED gets its SimpleDynel visual-change pushed (e.g. the beach chain-wall collision
		// dropping when the escort begins). Data-driven (WorldObject.EffectQuestId/EffectDynelState), no hard-coded
		// lookup. Called at world entry (a returning player isn't re-blocked by the burst) and on quest supersession /
		// completion (the change fires the instant the player reaches the quest state).
		public async Task ApplyQuestWorldEffects(INetworkClient client)
		{
			var character = client.Session.Character;
			var player = _sim.PlayerWorldEntity(client);
			if (character == null || player == null)
				return;

			var held = (await _database.GetCharacterQuests(character.Id)).Where(q => q.State is QuestState.Active or QuestState.Completed).Select(q => q.QuestId).ToHashSet();
			if (held.Count == 0)
				return;

			foreach (var obj in _sim.World.ZoneForEntity(player).WorldObjects)
			{
				if (obj.Row.EffectQuestId is not { } eq || !held.Contains(eq))
					continue;

				// The column stores the visual-change KIND, and the one value any row ships is SetKdTree - the chain
				// decor dropping its collision. Now that a kind selects a typed payload (#678) an unknown one cannot
				// be built blind, so it is reported rather than guessed at.
				if ((DynelVisualChange)obj.Row.EffectDynelState != DynelVisualChange.SetKdTree)
				{
					_logger.LogWarning("Quest world effect: '{Obj}' 0x{Handle:X8} asks for visual-change kind {Kind}, which carries a payload we have no source for - skipped.",
						obj.Row.Name, obj.Handle, obj.Row.EffectDynelState);
					continue;
				}

				await WorldIir.Send(client, SimpleDynelVisualChangeCodec.Build(SimpleDynelVisualChanges.SetKdTree(WorldObjectColumns.TypeTagForClass(obj.SubOpcode), obj.Handle, 0)));
			}
		}

		// #262: drop the reach-goal cache so the next move reloads it (a supersession may have granted a stage carrying
		// a new ReachArea goal). No-op if the player has no live entity yet.
		public void InvalidateReachGoals(INetworkClient client)
		{
			var player = _sim.PlayerWorldEntity(client);
			player?.PendingReachGoals = null!;
		}

		// #267: re-send one NPC's quest marker in-place (handles are session-volatile, so the NPC is resolved in the
		// player's zone rather than by handle). Unlike RefreshQuestGiverMarker this does NOT gate on the quest-GIVER map,
		// so it also drives the '?' talk marker for an NPC the player must talk to next (e.g. Casilda after Tuut's key,
		// which activates the talk stage while she's out of the conversation/stream-in path that would otherwise refresh
		// her marker).
		//
		// #521: resolved by TEMPLATE where the goal has one, name only as the fallback - a marker put on the wrong
		// same-named NPC points the player at a creature whose greeting will not advance anything.
		public async Task RefreshQuestMarkerFor(INetworkClient client, string npcName, uint? npcTemplateId = null)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || string.IsNullOrEmpty(npcName) && npcTemplateId is not > 0)
				return;

			var npc = npcTemplateId is > 0 and { } templateId
				? _sim.World.ZoneForEntity(player).Entities.FirstOrDefault(e => !e.IsPlayer && e.NpcTemplateId == templateId)
				: _sim.World.ZoneForEntity(player).Entities.FirstOrDefault(e => !e.IsPlayer && string.Equals(e.Name, npcName, StringComparison.OrdinalIgnoreCase));

			if (npc != null)
				await SendQuestMarker(client, npc.Handle, await ComputeGiverMarkerState(client, npc));
		}

		// #261: re-evaluate quest-gated NPC visibility for one player after their quest state changed (accept / advance /
		// complete). Reloads their active-quest set, then runs an interest pass so newly-qualified gated NPCs spawn and
		// no-longer-qualified ones despawn in-place - no player/NPC movement required.
		public async Task RefreshQuestVisibility(INetworkClient client)
		{
			var entity = _sim.PlayerWorldEntity(client);
			if (entity == null)
				return;

			await LoadActiveQuestIds(entity);
			var events = InterestManager.ResolveMove(_sim.World.ZoneForEntity(entity), entity, WorldSimulation.PlayerInterestRange, true);
			await DispatchInterest(events, null, client, entity);
		}

		// #778: re-run one player's interest pass with NOTHING having changed on their side - the world tick calls it on
		// a slow cadence so that entities which moved THEMSELVES are streamed in and out.
		//
		// Every other interest pass is driven by the player: BroadcastPlayerMove (they moved), BroadcastPlayerEntry
		// (they arrived), RefreshQuestVisibility (their quests changed). Nothing was driven by the SUBJECT moving, and
		// `BroadcastNpcMove` only sends to viewers who already hold the handle - so for a player standing still, an NPC
		// that walked out of range froze at its last rendered position for ever, and one that walked in was invisible
		// until the player next moved. #703 made that routine rather than rare: a route walker travels its whole beat,
		// and an Old Tarantia beat runs up to 98 waypoints.
		//
		// ⚠️ It cannot hang off the NPC's own move effect, which is what the obvious fix would be. A ROUTE walker emits
		// no per-step effect at all - the beat is issued to the client once and walked THERE (#716), and the server only
		// advances its model - so the entities most in need of this are exactly the ones with no move to hook.
		//
		// `moveBody: null` makes this Spawn/Despawn only; the movement itself already rides BroadcastNpcMove / the
		// issued patrol. The mover arguments are left null on purpose: they only drive world-OBJECT streaming, and a
		// static object cannot walk into range.
		public async Task ResolveInterest(INetworkClient client)
		{
			var entity = _sim.PlayerWorldEntity(client);
			if (entity == null)
				return;

			var events = InterestManager.ResolveMove(_sim.World.ZoneForEntity(entity), entity, WorldSimulation.PlayerInterestRange, true);
			await DispatchInterest(events, null);
		}

		// Loads a map's static world objects into its zone ONCE, the world-object twin of EnsureZoneNpcs (#424). Before
		// this the entry path re-queried them per world entry, which is also how a missing `Include(o => o.Words)`
		// managed to ship invisible doors, vines and destructibles (ef9b59de) - one load per zone is one place to get
		// that right.
		public async Task EnsureZoneWorldObjects(uint mapId, uint instanceId)
		{
			if (!_objectLoadedZones.TryAdd((mapId, instanceId), 0))
				return;

			var zone = _sim.World.ZoneFor(mapId, instanceId);
			zone.WorldObjects =
			[
				.. (await _database.GetWorldObjects(mapId)).Select(row => new LiveWorldObject(zone.AllocateHandle(), row)
				{
					// A harvest node starts FULL on every zone load (#494). Retail's nodes are shared across the whole
					// playfield and regrow, and neither the sharing nor a respawn cadence is measured - so a fresh zone
					// giving a full node is the closest honest model, and it means a harvested node comes back rather
					// than being gone for good. Null for everything that is not a node.
					Health = HarvestCatalog.IsHarvestable(row.DisplayCategory, row.DisplayId)
						? HarvestCatalog.NodeMaxHealth
						: null
				})
			];
		}

		// The world objects within interest range at ENTRY, as ready-to-send records in the position retail puts them -
		// early in the entry stream, before the snapshot. Mirrors BuildEntryNpcBurst: it seeds the known set so the
		// per-move pass afterwards streams only the delta. No despawns are possible on a fresh entry (the known set
		// starts empty), so any that appear would mean stale per-zone state and are returned for the caller to send.
		public (List<Rpc> Records, int Total) BuildEntryWorldObjects(INetworkClient client, WorldEntity player, IReadOnlySet<uint> quests)
		{
			var zone = _sim.World.ZoneForEntity(player);
			var (spawn, _) = InterestManager.ResolveWorldObjects(zone, player, WorldSimulation.PlayerInterestRange, quests);

			return ([.. spawn.Select(o => WorldObjectRpc(client, o, zone.PlayfieldToken))], zone.WorldObjects.Count);
		}

		// Brings the player's rendered world objects in line with interest after a move: spawns what came into range,
		// despawns what left. See ResolveWorldObjectInterest for why re-entry re-sends the whole record.
		public async Task StreamWorldObjects(INetworkClient client, WorldEntity player)
		{
			var zone = _sim.World.ZoneForEntity(player);
			if (zone.WorldObjects.Count == 0)
				return;

			// On a move the entity's own set is current - BroadcastPlayerMove refreshes it immediately before this.
			var (spawn, despawn) = InterestManager.ResolveWorldObjects(zone, player, WorldSimulation.PlayerInterestRange, player.ActiveQuestIds);

			foreach (var obj in spawn)
				await client.SendAsync(WorldObjectRpc(client, obj, zone.PlayfieldToken));

			foreach (var handle in despawn)
			{
				// Despawn with the object's OWN class - a Character-classed quit silently misses a world object (#366).
				var classWord = zone.TryGetWorldObject(handle, out var gone)
					? WorldObjectColumns.TypeTagForClass(gone.SubOpcode)
					: WorldObjectColumns.TypeTagForClass(0);
				await SendWorldDespawn(client, zone.PlayfieldToken, handle, classWord);
			}
		}

		// One static world object's FullUpdate, built from its columns and stamped with the live playfield token. The
		// carrier's [u32 len] is the SENDER's (#530 rule 2), so it is written here rather than by the codec.
		private static Rpc WorldObjectRpc(INetworkClient client, LiveWorldObject obj, uint playfieldToken)
		{
			return new Rpc(GameServerListener.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				// The door state comes from the LIVE object, not the row (#598) - a door this zone has open streams open.
				s => s.WriteArrayUInt32Length(WorldObjectCodec.Build(obj.Row, obj.Handle, playfieldToken, obj.IsClosed)));
		}

		// Sends a QuestFullUpdate (one quest's full detail: description, summary, objective + map marker) built from
		// its seeded definition.
		//
		// Nothing per-character rides along: what used to be passed here as an "accept time" is the goal record's
		// reserved (marker, 4CC-tag) pair, which is quest DATA and now comes off the goal row (#537).
		public Task SendQuestFullUpdate(INetworkClient client, QuestDefinition def, uint? level = null)
		{
			// The per-quest level is a constant harvested into the definition (#352); fall back to the captured default
			// only when this quest had no full-update in the captures (Level == 0).
			var levelValue = level ?? (def.Level != 0
				? def.Level
				: QuestFullUpdateCodec.DefaultQuestLevel);
			var body = QuestFullUpdateCodec.Build(QuestFullUpdates.ForQuest(client.Session.Character.Id, def, levelValue));

			return client.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(body)));
		}

		// Sends a QuestGoal (objective progress) to the player.
		public Task SendQuestGoal(INetworkClient client, uint questId, uint goalId, QuestGoalUpdate update)
		{
			return client.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(QuestGoalCodec.Build(client.Session.Character.Id, questId, goalId, update))));
		}

		// Sends a QuestIIR (add/update a quest in the player's journal) to the player.
		public Task SendQuestIir(INetworkClient client, uint questId, uint state)
		{
			return client.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(QuestCodec.Build(client.Session.Character.Id, questId, state))));
		}

		// Make the named NPC in `mapId`'s zone follow (or stop following) the given player handle - the quest-driven
		// entry point (e.g. Casilda after her rescue stage), keyed by NAME since handles are session-volatile. No-op
		// (returns false) if no such NPC is loaded in the zone.
		// #262: make the NPC of TEMPLATE id `templateId` in `mapId`'s zone follow (or stop following) the player - the
		// id-based escort companion trigger (prefer ids over names). The escortee's template is the Escort goal's
		// TrailingRefId (e.g. Casilda 9411). See SetNpcFollow for the instance-aware zone resolution.
		public bool SetNpcFollowByTemplate(uint mapId, uint templateId, uint playerHandle, bool follow)
		{
			return SetNpcFollow(mapId, playerHandle, follow, e => e.NpcTemplateId == templateId, $"template {templateId}");
		}

		// Called after items are taken from a bag (loot-all OR a single drag): confirm the take to the client with
		// LootRemoveItems, and tear the bag down ONLY once it is FULLY emptied (no items AND no coins). A partial loot
		// leaves the bag in the world with its remaining contents; it still fades on its own expiry (TickExpiry).
		// Retail loot-all finish decoded from the hoxxling gameplay capture (frame 4470): LootRemoveItems ->
		// DispatchCloseLootBag (0x2038) -> NewCurrentLooter/NewAllowedLooter reset -> n3ToClientFade -> UpdateSimpleDynel.
		public async Task AfterLootTake(INetworkClient client, uint bagHandle, params uint[] removedPositions)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.ZoneForEntity(player).TryGet(bagHandle, out var bag))
				return;

			// Per-take ACK: tell the client which display slot(s) to remove from the open window. The client removes
			// them and compacts; a partial take leaves the bag open with its remaining items - NO re-render (re-sending
			// the full object stacked a second window).
			//
			// The list is a REAL list, not a one-element formality (#453). Measured over all 148 LootRemoveItems
			// records in the corpus, the element count takes 0, 1, 2, 3 and 4, and the positions are the contiguous
			// run [0..n-1] in every record but one - the exception being loot-drag-1538's single drag of slot 1, which
			// sends exactly [1]. So a loot-all of n items is ONE message listing all n slots, not n messages, and this
			// used to send [0] for every loot-all: the client was told to drop the first slot and nothing else.
			await WorldIir.Send(client, LootRemoveItemsCodec.Build(bag.Handle, removedPositions ?? []));

			if (bag.LootItems.Any(i => i != 0) || bag.CashTin > 0)
				return; // items/coins remain (0 = a drag-looted slot) - keep the bag open; client drives close (0x2044)

			// Fully emptied -> the captured completion sequence: DispatchCloseLootBag -> looter resets -> fade ->
			// consumed visual. (LootRemoveItems with remaining=0 was already sent above.)
			await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x58), client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchCloseLootBag,
				b => b.WriteUInt32(EntityClass.LootBag).WriteUInt32(bag.Handle)));
			await WorldIir.Send(client, NewLooterCodec.Build(NewLooters.Current(bag.Handle, 0)));
			await WorldIir.Send(client, NewLooterCodec.Build(NewLooters.Allowed(bag.Handle, 0)));
			// ⚠️ The FADE and the consumed visual go to EVERYONE in range, not just the looter. The three messages
			// above are the looter's - their window closes, their looter rights reset - but the bag leaving the world
			// is a world event. Sent only to the looter, the bag stayed on every other player's screen, and with it
			// whatever the client draws on a lootable: the object is still there, so its effect is too.
			await BroadcastEntityFade(bag);

			_sim.World.ZoneForEntity(bag).Remove(bag.Handle);
			_logger.LogInformation("Loot: bag 0x{Handle:X8} fully looted; consumed + faded.", bag.Handle);
		}

		// The SOURCE NPC of a loot bag (a corpse bag REUSES the dead NPC's WorldEntity + handle, so its Name is the
		// NPC's, e.g. "Scavenger Tuut") - the match key for a LootFromNpc quest goal (#261), since the quest item itself
		// is RDB content we don't materialise. Both halves travel together (#521): the TEMPLATE is what a resolved goal
		// matches on and the NAME is the fallback for one whose target has no template row yet. (null, 0) for a non-bag
		// handle or a bag with no source.
		public (string? Name, uint TemplateId) BagSource(INetworkClient client, uint bagHandle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player != null && _sim.World.ZoneForEntity(player).TryGet(bagHandle, out var bag) && bag.IsLootable)
				return (bag.LootSourceName, bag.LootSourceTemplateId); // the dead NPC's identity, NOT the bag's "Bag of loot"

			return (null, 0);
		}

		// Relays a player's BEHAVIOR (the BehaviorControlCenterIIR swing/cast animation) to every other player who can
		// already see them, so the attack/cast renders on the other clients - the combat analogue of BroadcastPlayerMove
		// (#48). `body` is the same BCC echo the actor's own client receives (it already carries the actor's handle as
		// instance); retail relays that exact body to nearby players, only the frame receiver differs (the
		// other-players-visible capture shows s2c BCC for other-player handles 0x1316xxx). Gated on the viewer's Known
		// set so a still-loading client isn't sent a behavior for a handle it hasn't built. No-op solo / when unseen.
		public async Task BroadcastBehaviorToViewers(INetworkClient actor, byte[] body)
		{
			var entity = _sim.PlayerWorldEntity(actor);
			if (entity == null || body == null)
				return;

			var zone = _sim.World.ZoneForEntity(entity);
			if (zone == null)
				return;

			foreach (var viewer in zone.Entities)
			{
				if (!viewer.IsPlayer || viewer.Handle == entity.Handle || viewer.Client == null)
					continue;
				if (!viewer.Known.Contains(entity.Handle))
					continue; // only players who already see the actor (their client has the handle)

				await WorldIir.Send(viewer.Client, body);
			}
		}

		// Despawn a world-owned entity (a killed NPC, an expired loot bag) for every nearby player THAT ACTUALLY HAS IT
		// in their Known set. The Known guard is the #245 invariant: an n3ToClientQuit for a handle the recipient was
		// never sent crashes the client (`pcDynel == null`) - a still-loading player in range of an entity it never
		// received must not get a despawn for it. Interest is the sole NPC spawn path now, so Known is the authoritative
		// "was this entity sent to this client" record; the interest path (ResolveMove) already only despawns Known
		// handles, but this range-based world-tick path did not. Removes the handle as it despawns. Uses the same
		// n3ToClientQuit packet as player despawns.
		public async Task BroadcastEntityDespawn(WorldEntity entity)
		{
			foreach (var viewer in _sim.World.ZoneForEntity(entity).PlayersInRange(entity.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Known.Remove(entity.Handle))
					await SendWorldDespawn(viewer.Client, _sim.World.ZoneForEntity(viewer).PlayfieldToken, entity.Handle);
			}
		}

		// Fade an entity out for every player in range (n3ToClientFadeIIR_t) - the graceful despawn used for loot bags
		// (consumed or expired). Loot bags carry the 0xC87A typetag.
		//
		// ⚠️ The fade is HALF of retail's sequence, and sending it alone is what leaves a bag on screen. The captured
		// completion is `… -> n3ToClientFade -> UpdateSimpleDynel`, and the second message is the CONSUMED visual: a
		// fade makes the object go away gracefully, the update states that it is spent. The expiry path used to send
		// only the fade, so an expired bag differed from a looted one for no reason - and a client that keeps the
		// object keeps everything hanging off it, including whatever it draws on a lootable.
		public async Task BroadcastEntityFade(WorldEntity entity)
		{
			var fade = N3ToClientFadeCodec.Build(EntityClass.LootBag, entity.Handle);
			var consumed = UpdateSimpleDynelCodec.Build(entity.Handle);
			foreach (var viewer in _sim.World.ZoneForEntity(entity).PlayersInRange(entity.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client == null)
					continue;

				await viewer.Client.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, viewer.Client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
					s => s.WriteArrayUInt32Length(fade)));
				await viewer.Client.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, viewer.Client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
					s => s.WriteArrayUInt32Length(consumed)));
			}
		}

		// An NPC engaged a target: push its aggro to every viewer in range that sees it - a HateListUpdate (the NPC now
		// hates the target), a ThreatLevel, and the NPC's target LOCK, so the threat/aggro is visible the tick combat
		// starts (#216). Additive to the combat-mode weapon-draw; gated with the rest of the combat sim by the caller.
		//
		// The three go out in retail's order: `NecroL80_..._login_combat` shows LockedToChanged arriving right after a
		// HateListUpdate/ThreatLevel pair. LockedToChanged carries [npc handle] -> [target ref], and every captured
		// instance is a low NPC handle locking onto a PLAYER, which is the aggro moment (#405).
		//
		// ⚠️ It goes to VIEWERS, not just to the target, and that is measured rather than assumed: in
		// `login_2-clients_group-trade-friend` the same (npc, target) pair is sent on BOTH clients' streams (frames
		// 3146/3147 and 3394/3395) even though only one of them is the target. A single-client capture cannot tell
		// those apart, which is why the two-client one is the one that settles it.
		public async Task BroadcastNpcAggro(WorldEntity npc, WorldEntity target)
		{
			if (target == null)
				return;

			var hate = HateListUpdateCodec.Build(npc.Handle, target.Handle);
			var threat = ThreatLevelCodec.Build(npc.Handle, 1, 1);
			var lockedTo = LockedToChangedCodec.Build(npc.Handle, target.Handle);

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Known.Contains(npc.Handle))
				{
					await WorldIir.Send(viewer.Client, hate);
					await WorldIir.Send(viewer.Client, threat);
					await WorldIir.Send(viewer.Client, lockedTo);
				}
			}
		}

		// An NPC engaged or disengaged combat: push the combat-mode stat (stat 20) to every viewer in range that sees it,
		// so the client DRAWS (enter) or SHEATHES (exit) the NPC's weapon. Without it the NPC swings empty-handed (#218) -
		// the swing animation plays but no weapon is in hand because the client never put the NPC into combat stance.
		// Enter sends the captured melee-mob value (4); exit sends 0 (the idle default - a spawn carries no stat-20 record
		// at all, so 0 sheathes; the sheathe value isn't separately captured). Only viewers who already know the handle
		// get it (mirrors BroadcastNpcMove).
		public async Task BroadcastNpcCombatMode(WorldEntity npc, bool inCombat)
		{
			var stats = new CharacterStatBlock().Set(CharacterStatId.CombatMode, inCombat
				? NpcCombatModeValue
				: 0u);
			var body = StatUpdateCodec.Build(npc.Handle, stats);

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Known.Contains(npc.Handle))
					await WorldIir.Send(viewer.Client, body);
			}
		}

		// The "this entity is alive and idling" assert (#225): a short-form BehaviorControlCenterIIR (BehaviorId.BccAction)
		// naming the one fixed idle behaviour, sent to the client that has just CONFIRMED building this NPC's dynel.
		//
		// ⚠️ ONCE per entity per viewer, which is what retail does and is not what this used to do. It was streamed as a
		// per-NPC ~1/s heartbeat - and #716 then had to cap the aggregate, because in Old Tarantia that was 119.6
		// BCC/s at one client. Measured over the corpus (`--report ambient-animation`): 37,965 records over 7,969
		// actors, **p50 1 record per actor**, p90 2. Retail asserts it as an entity comes into view and stops.
		//
		// The "continuous ~10/s per rendered entity" #225 was filed on was ONE actor in one capture - the necromancer's
		// Nemedian Deathstalker pet, which takes 1,250 of that capture's 1,353 records in bursts (60 inside one second,
		// p50 gap 0.000s). Generalising that actor to every entity is what produced the flood. What genuinely recurs for
		// an ordinary NPC is the ambient ANIMATION, a different command - see BroadcastNpcAmbientAnimation (#766).
		//
		// Confirmed-gated by construction (the caller is the confirm), because a record for a Known-but-not-Confirmed
		// handle hits the pcDynel-NULL loader crash - the hang the original heartbeat experiment ran into.
		public Task SendNpcIdleAssert(INetworkClient recipient, WorldEntity npc)
		{
			ArgumentNullException.ThrowIfNull(recipient);
			ArgumentNullException.ThrowIfNull(npc);

			return WorldIir.Send(recipient, BehaviorControlCenterCodec.Build(BehaviorAnimations.Heartbeat(npc.Handle, ++npc.HeartbeatTick)));
		}

		// The NPC performs one of its creature's ambient idle animations (#766): a BehaviorId.Emote record naming the
		// animation the TICK picked and its own length, so the client both plays it and knows how long it runs. Same
		// Confirmed gating as the heartbeat - the animation plays ON the dynel, so a still-loading viewer has nothing to
		// play it on. No-op when the tick picked nothing (a creature with no captured animations never gets here).
		public async Task BroadcastNpcAmbientAnimation(WorldEntity npc)
		{
			ArgumentNullException.ThrowIfNull(npc);
			if (npc.AmbientAnimationPicked is not { } animation)
				return;

			var record = BehaviorControlCenterCodec.Build(
				BehaviorAnimations.AmbientEmote(npc.Handle, animation.AnimationId, npc.NextBehaviorPhase(), animation.DurationCentiseconds));

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Confirmed.Contains(npc.Handle))
					await WorldIir.Send(viewer.Client, record);
			}
		}

		// The defender's shield rose changed (an NPC engaged or its shift timer moved a guard; a PLAYER moved one of
		// theirs, #620): send the CombatShieldIIR to every viewer that has it CONFIRMED, the same gating as the
		// heartbeat - the rose is drawn ON the dynel, so a still-loading client has nothing to draw it over.
		//
		// The OWNER is skipped when the defender is a player: they set stat 0xA5 locally before sending it up, so
		// echoing it back would only re-apply the value they already drew.
		public async Task BroadcastShieldRose(WorldEntity entity)
		{
			ArgumentNullException.ThrowIfNull(entity);
			var record = CombatShieldCodec.Build(entity.Handle, entity.ShieldDistribution);

			foreach (var viewer in _sim.World.ZoneForEntity(entity).PlayersInRange(entity.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Handle == entity.Handle)
					continue;

				if (viewer.Client != null && viewer.Confirmed.Contains(entity.Handle))
					await WorldIir.Send(viewer.Client, record);
			}
		}

		// An NPC moved a retail-sized segment (chasing a target, or returning home): send its MoveTask to every player in
		// range that already sees it. The body is the 61-byte transform form for the SEGMENT (MoveFrom -> Position, facing
		// the travel direction); the throttle in WorldState.Tick (#272) only queues this once the NPC has covered
		// MoveBroadcastSegment (~6u, retail's chase step) since the last one, so the client interpolates a real segment
		// over time instead of getting a 1.2u snap every tick (the jitter/teleport). Only viewers who know the handle get
		// it (else they'd get a move for an unknown entity).
		public async Task BroadcastNpcMove(WorldEntity npc)
		{
			// #272: send the mode-1 PathMoveTask WALK form (the client interpolates the segment over time) rather than the
			// mode-2 RotateMoveTask transform, which is a position-SET the client SNAPS to. The segment is MoveFrom ->
			// Position (the ~6u the throttle accumulated), so the client renders the NPC where the server model has it -
			// walking that segment - rather than lagging behind a far-goal path while the model jumps into melee (which
			// desyncs: the player takes hits from an NPC the client still shows mid-walk). The earlier path attempt reverted
			// because its trailer anchored at the START (walk then back = net-zero); BuildWalkBody anchors it at the segment
			// end (Position). Residual: consecutive segments still re-spline (a small snap) - the remaining #272 polish.
			var body = MoveTaskCodec.Build(new MoveTaskIIR(npc.Handle, npc.MoveFrom, MoveTaskNodes.Walk(npc.MoveFrom, npc.Position)));

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Known.Contains(npc.Handle))
					await WorldIir.Send(viewer.Client, body);
			}
		}

		// #266: (re-)issue a patrolling NPC's captured route to every in-range viewer that already sees it, so it keeps
		// walking its loop (the tick fires this on each patrol cycle; retail re-paths idle patrols). Only known handles.
		public async Task BroadcastNpcPatrol(WorldEntity npc)
		{
			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Known.Contains(npc.Handle))
					await SendPatrol(viewer.Client, npc);
			}
		}

		// An NPC respawned (its timer elapsed on the tick): re-render it for every player in range by re-sending its
		// stored FA015807 spawn body (re-framed + position-patched per recipient by SendWorldSpawn).
		public async Task BroadcastNpcRespawn(WorldEntity npc)
		{
			if (npc.SpawnBody == null)
				return;

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				// Re-add to Known: the corpse despawn (ConvertCorpseToLoot, #246) removed the handle, so the player
				// no longer has it. Known.Add gates the spawn to viewers who actually need it + restores the #245
				// invariant (Known == "was sent"), so later move/combat broadcasts (which check Known) reach it.
				if (viewer.Client != null && viewer.Known.Add(npc.Handle))
					await SendWorldSpawn(viewer.Client, npc); // #266: no route replay - the tick drives a server-side wander
			}
		}

		public async Task BroadcastNpcStop(WorldEntity npc)
		{
			var facing = npc.MoveFrom.FaceTowards(npc.Position);
			var record = StopCodec.Build(npc.Handle, npc.Position, facing);

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Known.Contains(npc.Handle))
					await WorldIir.Send(viewer.Client, record);
			}
		}

		// On disconnect/logout: despawn the leaving player for everyone who could see it (n3ToClientQuit), before it is
		// removed from the zone. Returns the dispatch task so the caller can fire-and-forget (the connection is going
		// away). Single-player is a no-op (no viewers).
		public Task BroadcastPlayerDespawn(WorldEntity leaver)
		{
			var events = InterestManager.ResolveRemove(_sim.World.ZoneForEntity(leaver), leaver);
			return DispatchInterest(events, null);
		}

		// After world entry: spawn the entering player to nearby OTHER players (its NPC set now rides the entry burst,
		// #398 BuildEntryNpcBurst, and is already in Known - so the interest pass streams no NPCs, only section 2's
		// spawn-me-to-viewers fires). Interest remains the sole path for the STREAMED delta as the player then walks.
		public async Task BroadcastPlayerEntry(INetworkClient client)
		{
			// No SpawnBody check any more: a player's record is BUILT per send (#429), so having one was never the real
			// precondition - having a live entity is.
			var entity = _sim.PlayerWorldEntity(client);
			if (entity == null)
				return;

			var zone = _sim.World.ZoneForEntity(entity);

			// The burst NPCs are already in Known (BuildEntryNpcBurst), so includeNonPlayers here re-confirms them in
			// range without re-spawning; its real job at entry is section 2 - spawning this player to nearby viewers.
			var events = InterestManager.ResolveMove(zone, entity, WorldSimulation.PlayerInterestRange, true, true);
			await DispatchInterest(events, null);
		}

		// After a player's Move: update interest and broadcast the resulting spawns/moves to nearby players.
		//
		// `moveBody` is the mover's own CharDCMove carrier VERBATIM (only the env byte cleared - the caller's echo body),
		// not a reconstruction. Retail relays a move byte-for-byte: across the corpus 9,438 of 9,932 c2s moves reappear
		// s2c identical bar that byte, and in xxx_2clients_jumping all 35 do, to BOTH clients. That also spares this end
		// the flag-gated layout - how many rotation floats flag1 bit4 implies, where a jump's acceleration vector sits
		// (docs/re/chardcmove-grammar.md) - which the old rebuild-from-parsed-fields had to know and got wrong.
		// `position` is the ACCEPTED position (the same one the body carries) and drives interest only.
		public async Task BroadcastPlayerMove(INetworkClient client, byte[] moveBody, Vector3 position)
		{
			var entity = _sim.PlayerWorldEntity(client);
			if (entity == null)
				return;

			// Advance the player's model position to where they actually are BEFORE computing interest - otherwise it
			// stays frozen at the entry point and only entity ever within range of the wash-ashore spot streams (the
			// "most NPCs missing" bug). This is also what makes other players see this one at its live position.
			entity.Position = position;

			// includeNonPlayers: stream the zone's NPCs to this player as they move (the burst only rendered the NPCs
			// near the entry point; this fills in the rest of the map - e.g. Casilda, who is outside the captured
			// burst). Burst NPCs are already in the player's Known set (seeded at entry) so they are not re-spawned.
			var events = InterestManager.ResolveMove(_sim.World.ZoneForEntity(entity), entity, WorldSimulation.PlayerInterestRange, true);
			var spawns = 0;
			var despawns = 0;
			foreach (var ev in events)
			{
				if (ev.Recipient.Handle == entity.Handle && ev.Type == InterestEventType.Spawn)
					spawns++;
				else if (ev.Recipient.Handle == entity.Handle && ev.Type == InterestEventType.Despawn)
					despawns++;
			}

			if (spawns > 0 || despawns > 0)
			{
				_logger.LogInformation("Interest: {Player} at {Pos} -> streamed +{Spawns} / -{Despawns} entities (now knows {Known}).", entity.Name, entity.Position, spawns, despawns,
					entity.Known.Count);
			}

			await DispatchInterest(events, moveBody, client, entity);
		}

		// Relays a player's ApplySpell (the MagicBuffIIR spell EFFECT - a buff applied, or an offensive cast) to every
		// other player who can already see the caster, so the effect renders on their clients too. This is the EFFECT
		// analogue of BroadcastBehaviorToViewers, which relays only the cast GESTURE (the BCC animation): retail streams
		// BOTH to nearby observers. `body` is the same ApplySpell carrier the caster's own client receives - it already
		// carries the acting player's handle as applyFrom (and, for a self-buff, applyTo); retail relays that exact body
		// to nearby players, only the frame receiver differs. Ground truth: the other-players-visible capture
		// (Hoxxling ...other_playersvisible) shows s2c ApplySpell (0x6B87E316) for other-player handles 0x1316xxx framed
		// to our endpoint - both self-buffs (e.g. player 0x01316e68 at frame 3809) and offensive casts (player
		// 0x01316cee on NPC targets). Gated on the viewer's Known set (#245: never relay for a handle the viewer's
		// client hasn't built). No-op solo / when the caster is unseen.
		// Knockback (#496): shove a PLAYER from where they stand to `to`, and make everyone who can see them watch it.
		//
		// Two records, in retail's own order (the annotated dungeon capture, 18:49:54.284 -> 18:49:56.020):
		//   1. the forced-move MoveTask (mode 0x05) carrying from -> to, which is what the client ANIMATES;
		//   2. a SetPos pinning the destination ~1.7 s later, which is what makes it authoritative.
		//
		// Both go to the victim AND to nearby viewers - the victim's client plays the shove, and a viewer who only got
		// the SetPos would see the other player teleport. The model position is advanced first so interest, aggro and
		// the next move all agree with where the player now is; skipping that is how a displaced player ends up
		// invisible to the NPCs still chasing their old position.
		//
		// ⚠️ The caller is expected to have ground-snapped `to`. This does NOT clamp it: a knockback is allowed to put
		// a player somewhere they could not have walked (off a ledge - which then costs them fall damage, #495), and
		// clamping here would quietly cancel that.
		public async Task BroadcastKnockback(WorldEntity victim, Vector3 to)
		{
			var from = victim.Position;
			var move = MoveTaskCodec.Build(new MoveTaskIIR(victim.Handle, from, MoveTaskNodes.ForcedMove(to, from)));
			var pin = SetPosCodec.Build(victim.Handle, to, default, 0x01);

			victim.Position = to;

			if (victim.Client != null)
			{
				await WorldIir.Send(victim.Client, move);
				await WorldIir.Send(victim.Client, pin);
			}

			var zone = _sim.World.ZoneForEntity(victim);
			if (zone == null)
				return;

			foreach (var viewer in zone.Entities)
			{
				if (!viewer.IsPlayer || viewer.Handle == victim.Handle || viewer.Client == null)
					continue;
				if (!viewer.Known.Contains(victim.Handle))
					continue; // only players whose client already holds the victim's handle

				await WorldIir.Send(viewer.Client, move);
				await WorldIir.Send(viewer.Client, pin);
			}

			_logger.LogInformation("Knockback: {Victim} shoved {Distance:F2} units from {From} to {To}.", victim.Name, Vector3.Distance(from, to), from, to);
		}

		public async Task BroadcastSpellToViewers(INetworkClient caster, byte[] body)
		{
			var entity = _sim.PlayerWorldEntity(caster);
			if (entity == null || body == null)
				return;

			var zone = _sim.World.ZoneForEntity(entity);
			if (zone == null)
				return;

			foreach (var viewer in zone.Entities)
			{
				if (!viewer.IsPlayer || viewer.Handle == entity.Handle || viewer.Client == null)
					continue;
				if (!viewer.Known.Contains(entity.Handle))
					continue; // only players who already see the caster (their client has the handle)

				await WorldIir.Send(viewer.Client, body);
			}
		}

		// A player's STAT change that other players must see - the mount being the case that exposed the gap (#428): the
		// ride buff was already relayed by SendMagicBuffIir, but the StatUpd carrying Mount/MountSpeed was not, and that
		// is the half the client renders the horse from. So the actor sat on a mount only on their own screen.
		//
		// The stat body names the ENTITY, and the client argument only frames the receiver, so the same block is rebuilt
		// per viewer - exactly what BroadcastSpellToViewers does with the ApplySpell body. Same #245 gate: never relay
		// for a handle the viewer's client has not built.
		public async Task BroadcastStatsToViewers(INetworkClient actor, CharacterStatBlock stats)
		{
			var entity = _sim.PlayerWorldEntity(actor);
			if (entity == null)
				return;

			foreach (var viewer in _sim.World.ZoneForEntity(entity).Entities)
			{
				if (!viewer.IsPlayer || viewer.Handle == entity.Handle || viewer.Client == null)
					continue;
				if (!viewer.Known.Contains(entity.Handle))
					continue;

				await viewer.Client.SendAsync(GameServerListener.BuildStatUpdate(viewer.Client, stats, entity.Handle));
			}
		}

		// #398: builds the in-range NPC spawns for the world-ENTRY BURST - the close NPCs retail ships in the burst
		// BEFORE the client's first move (fr64-75 in the Hoxxling beach login capture), instead of dribbling them out
		// as a post-load interest volley. Each is SEEDED into the entering player's Known set so the interest layer
		// (BroadcastPlayerMove) streams only the DELTA as the player walks and never re-spawns or culls the initial
		// set - which subsumes the #245 despawn-during-load root cause. Registers each handle -> name/template for
		// click routing exactly as DispatchInterest does for a streamed spawn. Returns the framed FA015807 records to
		// append to the entry burst (`toSend`) plus the NPCs themselves, whose quest markers / captive pose ride
		// SendEntryNpcFollowups after the burst flush (those are separate IIRs, not spawn records). The entering
		// player is spawned to OTHER viewers separately (BroadcastPlayerEntry, section 2).
		public async Task<(List<Rpc> Records, List<WorldEntity> Npcs)> BuildEntryNpcBurst(INetworkClient client)
		{
			var records = new List<Rpc>();
			var npcs = new List<WorldEntity>();

			var entity = _sim.PlayerWorldEntity(client);
			if (entity?.SpawnBody == null)
				return (records, npcs);

			// Seed the player's active/completed quest set first so quest-gated NPCs (#261 Scavenger Tuut, #262 hide-
			// once-done) are correctly included/excluded in the burst from the very start.
			await LoadActiveQuestIds(entity);

			var zone = _sim.World.ZoneForEntity(entity);
			var token = zone.PlayfieldToken;
			foreach (var npc in zone.EntitiesInRange(entity.Position, WorldSimulation.PlayerInterestRange, entity.Handle))
			{
				if (npc.IsPlayer || npc.IsLoot || npc.IsDead || npc.SpawnBody == null)
					continue;
				if (!npc.IsVisibleTo(entity))
					continue; // #261: a quest-gated NPC the player doesn't qualify for yet - treat as not in range
				if (!entity.Known.Add(npc.Handle))
					continue; // already seeded (idempotent guard)

				if (npc.Name != null)
				{
					client.Session.SpawnedNpcNamesByHandle[npc.Handle] = npc.Name;
					client.Session.SpawnedNpcTemplatesByHandle[npc.Handle] = npc.NpcTemplateId;
				}

				records.Add(BuildWorldSpawnRecord(client, npc, token));
				npcs.Add(npc);
			}

			return (records, npcs);
		}

		// A player closed the loot window (top-level 0x2044). What happens to the bag depends on what is LEFT in it, not
		// on the close: an EMPTY bag is consumed (faded for nearby players and dropped from the model), one that still
		// holds items or coins stays in the world so the player can come back to it.
		//
		// Closing the window is not taking the loot. This used to fade + remove unconditionally, so pressing Escape
		// deleted everything the player had not taken yet.
		//
		// The cases, all ending at "emptied -> gone, not emptied -> stays":
		//   loot-all           -> bag emptied -> AfterLootTake tears it down; this handler then finds nothing, no-ops
		//   drag the last item -> same, AfterLootTake
		//   drag 1 of 2        -> contents remain -> kept, and it expires on its own (TickExpiry -> LootExpired)
		//
		// Nothing is consumed HERE, not even a bag that reads as empty. A no-drop kill leaves no bag at all
		// (ConvertCorpseToLoot only spawns one for questDrop || items || cash), so the only bag that can be open and
		// empty is the QUEST one: a LootFromNpc mob whose goal names no quest item drops an empty bag deliberately,
		// because looting it is what completes the goal (the goal matches on the bag's SOURCE NPC, not its contents).
		// Consuming that on close would make the quest uncompletable - the player closes the window once and the goal
		// has nowhere left to be satisfied. Letting it expire instead costs nothing and leaves them a way back.
		//
		// ⚠️ Whether RETAIL keeps a part-looted bag is not something the captures can tell us: all 95 closes in the
		// corpus are of a bag that was just emptied (89 follow a TryLootAll, 6 follow drags that took what was there),
		// and what they show - fade ~0.2s after the close, n3ToClientQuit ~3.4s after the fade - is the same empty-bag
		// teardown AfterLootTake already reproduces. Gitea #431 asks for the capture that would decide it.
		public Task CloseLootBag(INetworkClient client, uint bagHandle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.ZoneForEntity(player).TryGet(bagHandle, out var bag) || !bag.IsLootable)
				return Task.CompletedTask;

			_logger.LogInformation("Loot: {Name} closed the window on bag 0x{Handle:X8}; {Items} item(s) + {Cash} tin left in it, kept until it expires.", player.Name, bag.Handle,
				bag.LootItems.Count(i => i != 0), bag.CashTin);
			return Task.CompletedTask;
		}

		// A dead NPC's death-animation linger elapsed (driven by the world tick): turn its corpse into a lootable bag
		// IN PLACE. Every kill leaves a lootable corpse - the drop ROLL decides only whether it carries items/cash, not
		// whether it's lootable (retail leaves a lootable corpse on every kill; empty corpses are fine). Falls back to a
		// plain despawn only when no loot-bag world object can be built for the map.
		public async Task ConvertCorpseToLoot(WorldEntity deadNpc)
		{
			if (!deadNpc.IsDead)
				return; // revived (or never died) before the linger fired - nothing to convert

			// A quest mob the killer needs to LOOT (an active LootFromNpc goal targeting this NPC, #261) ALWAYS leaves a
			// bag holding the goal's QUEST ITEM - it must be lootable, not a 50/50 roll.
			var lootGoalItem = deadNpc.LastAttackerHandle is { } looterHandle
				? await ActiveLootGoalItemFor(looterHandle, deadNpc.Name, deadNpc.NpcTemplateId)
				: null;
			var questDrop = lootGoalItem.HasValue;

			List<uint> items;
			uint cash;
			if (questDrop)
			{
				// Quest loot mob: the bag holds ONLY the goal's quest item (no random gear/cash), so the objective is
				// unambiguous - e.g. Scavenger Tuut drops just the Scavenger's Key. ActiveLootGoalItemFor only returns a
				// value when the goal names one, so there is no empty-quest-bag case to model.
				items = [lootGoalItem!.Value];
				cash = 0;
			}
			else
			{
				var dropsContents = LootResolver.RollsDrop(Random.Shared.NextDouble());
				items = await RollLootItems(dropsContents
					? Random.Shared.Next(0, 3)
					: 0, deadNpc.MapId); // 0-2 items, or none
				cash = dropsContents
					? LootResolver.RollCash(Random.Shared.NextDouble(), deadNpc.Level)
					: 0u;
			}

			// DECOUPLED loot (#246): the bag is a SEPARATE IsLoot entity (its OWN handle) at the death spot, NOT the
			// NPC's handle reused. Reusing it pinned the NPC's respawn to where it died (the client can't move a bag
			// handle via the respawn's CharFullUpdate) and made the loot vanish when the NPC respawned. Spawn the bag,
			// then despawn the corpse so the NPC handle respawns clean at its spawn point (BroadcastNpcRespawn re-adds
			// it to Known); the bag lives/loots/expires on its own (tick-managed via TickExpiry). Empty corpses just
			// despawn (matching retail - not every kill leaves a bag).
			// Spawn a bag when there's loot, OR unconditionally for a quest mob (the bag may be empty - looting it still
			// completes the LootFromNpc goal, which matches on the bag's source NPC, not its contents).
			if (questDrop || items.Count > 0 || cash > 0)
			{
				// Bag at the NPC's death spot HORIZONTALLY (X/Z - where the player expects it, not at the killer's feet
				// on a ranged/AoE kill), snapped to the terrain GROUND Y so a static bag mesh sits on the floor instead
				// of at the creature's death-moment Y (which varies by creature: tall / flying / MoveTask Z-drift #190 ->
				// bags float or sink). #284: the ground height is the RDB heightmap sample at the death X/Z.
				//
				// The snap only applies when the heightmap AGREES the NPC was standing on the outdoor surface. The
				// heightmap is that surface and nothing else: an NPC killed in a cave, indoors, or on an upper floor
				// sits far below (or above) it, and snapping there flings the bag to the open air - Kier-Dheed died at
				// Y=153 under a surface of Y=181 and his bag spawned 28m overhead, unreachable. Outdoors the delta is
				// ~1m, which is why this only ever showed up on interior kills. Past the tolerance the death Y is the
				// better answer: the creature was demonstrably standing there.
				var dropPos = deadNpc.Position;
				if (_sim.Terrain?.GroundHeight(deadNpc.MapId, deadNpc.Position.X, deadNpc.Position.Z) is { } groundY && MathF.Abs(groundY - deadNpc.Position.Y) <= MaxLootGroundSnap)
					dropPos = deadNpc.Position with { Y = groundY };

				var bag = await SpawnLootBagAt(deadNpc.MapId, deadNpc.InstanceId, dropPos, deadNpc.Rotation, items, cash, deadNpc.LastAttackerHandle ?? 0, deadNpc.Name, deadNpc.LootContainer,
					deadNpc.NpcTemplateId);
				_logger.LogInformation(
					"Loot: {Name} 0x{Handle:X8} dropped bag 0x{Bag:X8} ({Items} items, {Cash} tin, looter 0x{Looter:X8}, quest={Quest}) on map {Map} at {Drop} (death spot {Death}).", deadNpc.Name,
					deadNpc.Handle, bag?.Handle ?? 0, items.Count, cash, deadNpc.LastAttackerHandle, questDrop, deadNpc.MapId, dropPos, deadNpc.Position);
			}

			await BroadcastEntityDespawn(deadNpc); // remove the corpse render; the NPC revives via TickRespawn (or is removed if NoRespawn)
		}

		// GM .delete of a static WORLD OBJECT (chest/door/dynel): these aren't in the live entity model (they stream
		// once at world entry), so a handle that isn't a live entity may still be one. Despawns it live for nearby
		// players AND deletes its DB row so it's gone on relog too. Returns the deleted row, or null if the handle isn't
		// a known world object on the player's map.
		public async Task<WorldObject?> DeleteWorldObjectForPlayer(INetworkClient client, uint handle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return null;

			var zone = _sim.World.ZoneForEntity(player);
			if (!zone.TryGetWorldObject(handle, out var live))
				return null;

			var obj = live.Row;

			// World objects aren't tracked in Known, so we can't use that #245 gate; instead only despawn for viewers
			// that have ENTERED the world (they received the world-object burst, so the handle is safe to quit for them).
			// A world object is removed by its OWN class (SimpleDynel / LootBag / ZoneLabel / ...), not Character - a
			// Character-classed despawn silently misses it (#366). Derive the class from its spawn IIR.
			var classWord = WorldObjectColumns.TypeTagForClass(obj.SubOpcode);
			foreach (var viewer in zone.PlayersInRange(obj.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client.Session.WorldEntered)
					await SendWorldDespawn(viewer.Client, zone.PlayfieldToken, handle, classWord);
			}

			zone.RemoveWorldObject(handle); // stop streaming it without waiting for a zone reload
			var deleted = await _database.DeleteWorldObject(obj.Id);
			_logger.LogInformation("Delete: removed world object {Name} 0x{Handle:X8} ({Rows} row(s), by {Player}).", WorldObjectDisplay.ToWire(obj) ?? obj.Name, handle, deleted, player.Name);
			return obj;
		}

		// Despawns a non-player entity by handle in the calling player's zone: the decoded n3ToClientQuit removal
		// to every player in range + drop from the model (the cleanup complement to SpawnNpcForPlayer; also works
		// on burst NPCs and loot bags). Returns the removed entity, or null when the handle is unknown or a player.
		public async Task<WorldEntity?> DespawnEntityForPlayer(INetworkClient client, uint entityHandle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.ZoneForEntity(player).TryGet(entityHandle, out var entity) || entity.IsPlayer)
				return null;

			foreach (var viewer in _sim.World.ZoneForEntity(player).PlayersInRange(entity.Position, WorldSimulation.PlayerInterestRange))
			{
				// #245 invariant: only despawn for a viewer that actually has this handle (a despawn for a handle the
				// recipient was never sent crashes it). Removes it from Known as it goes.
				if (viewer.Client != null && viewer.Known.Remove(entityHandle))
					await SendWorldDespawn(viewer.Client, _sim.World.ZoneForEntity(viewer).PlayfieldToken, entityHandle);
			}

			_sim.World.Remove(entity);
			_logger.LogInformation("Despawn: removed {Name} 0x{Handle:X8} (by {Player}).", entity.Name, entityHandle, player.Name);
			return entity;
		}

		// Why a class-specific destination may not be enterable yet, or null when it is.
		//
		// A class-specific playfield (Map.ClassSpecific - the city armories, and The Reliquary) holds a different NPC
		// set per class, and we
		// only have the classes some capture actually walked into. Teleporting a class we hold no set for would drop the
		// player into an empty room - and on a map with no captured world objects either, one with no door back out. So
		// the zone-line reports the gap in-game instead of teleporting, the same rule as an un-imported vendor.
		public async Task<string?> DescribeMissingClassImport(uint mapId, ConanClass forClass)
		{
			var map = await _database.GetMap(mapId);
			if (map is not { ClassSpecific: true })
				return null;

			var imported = await _database.GetImportedSpawnClasses(mapId);
			if (imported.Contains(forClass))
				return null;

			var have = imported.Length > 0
				? $"imported so far: {string.Join(", ", imported.Select(c => c.ToString()).OrderBy(n => n))}"
				: "no class imported yet";
			var name = string.IsNullOrEmpty(map.Name)
				? $"Map {mapId}"
				: map.Name;
			return $"{name} is class-specific and has no import for {forClass} - {have}.";
		}

		// Loads a map's NPCs from NpcSpawns into the world model ONCE (on first entry), so the server has an
		// authoritative shared view of the map's NPCs - the model fix at the heart of the roadmap (NPCs are otherwise
		// only per-player burst replays the server doesn't track). The tick/AI/combat will drive these; clients still
		// render them from the burst for now, so this is model bookkeeping (no wire change).
		//
		// `forClass` is the class of the character this copy is being populated for. It only matters on a
		// class-specific map (Map.ClassSpecific - the city armories and The Reliquary), where the playfield holds a
		// different NPC set per
		// class; such a map is necessarily Solo, so the copy has exactly one owner and one class. Everywhere else every
		// spawn is class-agnostic and this is ignored.
		public async Task EnsureZoneNpcs(uint mapId, uint instanceId, ConanClass forClass)
		{
			if (!_npcLoadedZones.TryAdd((mapId, instanceId), 0))
				return;

			var npcs = await _database.GetNpcSpawns(mapId, forClass);

			// #502: the map's scripted encounters, by LURE type - one query per zone load rather than per placement.
			var encounters = (await _database.GetNpcEncounters(mapId)).ToDictionary(e => e.LureNpcTemplateId);

			// #528: the map's TRIGGER-SPAWNED types - a quest ambusher and an encounter BOSS alike. Neither stands in
			// the world (one appears when a goal advances, the other when a lure swaps), so a captured "placement" of
			// one is a harvest of the trigger FIRING, and it is skipped below. The importer already excludes them from
			// the burst-truth set (ImportOps.BuildBurstTruthSpawns, alongside the summoned pets), so on a current
			// database this finds nothing; it is the second net, for a database built before that rule.
			var triggerSpawned = (await _database.GetNpcAmbushes(mapId)).Select(a => a.NpcTemplateId)
				.Concat(encounters.Values.Select(e => e.BossNpcTemplateId))
				.ToHashSet();
			var zone = _sim.World.ZoneFor(mapId, instanceId);

			// #363 dev labels: the map's RDB spawn areas, so each NPC carries its area label ready for the live
			// `.togglenpcgroup` toggle. Computed at load (a cheap query, stored as a string) so the toggle is instant and
			// also catches NPCs that stream in while it's on - the toggle only gates whether the label is SENT.
			var spawnAreas = await _database.GetNpcSpawnAreas(mapId);

			// #261 quest-gated spawns: NPC template id -> the quest ids that reveal it (kill-target NPCs that aren't givers).
			_questGatedNpcs ??= await _database.GetQuestGatedNpcMap();
			_escortHiddenNpcs ??= await _database.GetEscortHiddenNpcMap();

			// NpcSpawns is the SOURCE OF TRUTH: the importer's burst-truth extraction (#266, BuildBurstTruthSpawns)
			// already produced one clean row per intended placement (keyed by MapId + exact XYZ), so this load path
			// renders EVERY row verbatim - NO runtime dedup. The old name/cell collapse silently dropped legitimately
			// distinct NPCs: neutral animals of the same name (5 Hens, 4 Scorpions), and faction guards standing 1-4
			// units apart (the Tarantia Noble District line and the Castle of King Conan door pairs). Any genuine
			// over-density is a data bug fixed in the importer, never hidden here.
			//
			// Wire handles are MINTED here (Zone.AllocateHandle), never read off the row. They used to be the spawn's
			// CapturedHandle - whichever handle the capture the importer last saw it in happened to assign - which is a
			// session-volatile value serving as a persistence key, and it was not even unique across the merged DB, so
			// this loop needed a collision dance to stop one row silently overwriting another in the handle-keyed zone.
			// The zone's counter is unique by construction and shared with the static objects, which the two separate
			// captured ranges never guaranteed against each other.
			// #713: rows sharing a PlacementId are SIGHTINGS of one patroller, not separate NPCs - the importer says
			// so, having identified them by their harvested beat. One entity per placement, and the row it renders as
			// is drawn from the others: they differ in exactly what retail re-rolls per spawn (level, morphs, all 7
			// equipment slots), so the pool is what makes two spawns of one placement look different.
			//
			// This is NOT the runtime dedup the note above forbids. That rule is about this layer inventing a collapse
			// (the old name/cell heuristic, which silently dropped legitimately distinct NPCs); this renders a
			// relationship the DATA declares. A row with no PlacementId stands for itself and is rendered verbatim;
			// each pool contributes ONE row, drawn at random, so the beat gets one NPC wearing one of its rolls.
			var spawnRows = npcs
				.Where(n => n.PlacementId == null)
				.Concat(npcs.Where(n => n.PlacementId != null)
					.GroupBy(n => n.PlacementId!.Value)
					.Select(pool => pool.ElementAt(Random.Shared.Next(pool.Count()))))
				.ToList();

			var labelledCount = 0; // #363 diagnostic: how many captured spawns got a non-null area label this load
			string? labelSample = null;
			var followers = new Dictionary<WorldEntity, HashSet<uint>>(); // #741: follower -> the leader TYPES it may bind to
			foreach (var npc in spawnRows)
			{
				// #528/#501: this type only exists inside a triggered spawn, so a row for it is not a creature standing
				// in the world.
				if (triggerSpawned.Contains(npc.NpcTemplateId))
					continue;

				var npcName = npc.NpcTemplate.Name;

				// Seed killable vitals from the NPC's REAL max health (lifted from the spawn into NpcSpawn.Health by the
				// importer), falling back to a level-scaled value when the spawn carried none.
				var maxHealth = npc.Health > 0
					? npc.Health
					: CombatResolver.NpcMaxHealthForLevel(npc.Level);
				var vitals = EntityVitals.FromMax(maxHealth, 0, maxHealth);
				// World entry is burst-less: every NPC streams in on the player's first move (interest), under the handle
				// this zone mints for it.
				var handle = zone.AllocateHandle();

				var entity = WorldEntity.ForNpc(handle, mapId, npc.Position, npc.Rotation, npcName ?? "NPC", vitals, npc.NpcTemplateId);
				entity.InstanceId = instanceId; // this NPC belongs to THIS copy of the map (#268)
				// #671: a mob that SHOOTS deals about a fifth of what one that swings does. Decided here, off the
				// equipment rows this query already Includes, so the combat tick reads a flag rather than a join.
				entity.RangedAttacker = NpcWeapon.IsRanged(npc.Equipment);
				// #623: and which `combo_npc_generic_*` its swings perform, off the same rows.
				entity.MeleeComboId = await _combos.NpcMeleeComboId(npc.Equipment);
				entity.Level = npc.Level;
				// #675: and the class-family rows it may swing instead, level-gated on the level just stamped.
				entity.ClassComboIds = await _combos.NpcClassComboIds(npc.Equipment, entity.Level);
				// #261: if this NPC's spawn is quest-gated, stamp the revealing quest ids so interest only streams it to
				// players holding one of them (e.g. Scavenger Tuut while you carry Casilda's kill quest).
				// #262: an escortee NPC vanishes once its escort quest is COMPLETED (e.g. Casilda after the beach escort);
				// keyed by template id, complement of the "show while active" gate below.
				if (_escortHiddenNpcs.TryGetValue(npc.NpcTemplateId, out var hideQuest))
					entity.HideWhenQuestCompletedId = hideQuest;

				if (_questGatedNpcs.TryGetValue(npc.NpcTemplateId, out var gateQuests))
				{
					entity.GateQuestIds = gateQuests;
					// Quest-gated NPCs are one-time KILL TARGETS (the gate map is exactly the kill-goal targets that
					// aren't quest givers). A story kill target must NOT respawn ~15s later - killing Scavenger Tuut
					// for Casilda's quest completes the goal and he should stay dead (retail solo-instances him). So a
					// gated NPC despawns after death instead of reviving; its loot bag (the quest key) is a separate
					// entity and still drops.
					entity.NoRespawn = true;
				}

				// #266 patrol: attach this NPC's captured route (linked by exact spawn position). REPLAY the captured
				// MoveTask re-addressed to this entity (handle + from = its spawn) - the original form carries the real
				// speed/flags, so the client walks the route at the right pace (a rebuilt mode-1 path lost the speed and
				// run-crawled). The cycle timer (path time + dwell) re-issues it so it keeps looping.
				if (npc.PatrolNodes is { Count: > 0 } patrol)
				{
					var waypoints = PatrolRoute.Waypoints(patrol);
					if (waypoints.Count > 0)
					{
						entity.PatrolWaypoints = waypoints;
						// #703: an ORDERED route, so the tick WALKS it waypoint by waypoint instead of drifting to a
						// random one of its waypoints - that straight line is what sent Old Tarantia's guards through
						// the houses. A route whose ends meet is walked round and round; an open one is walked back
						// down again; either way it joins at the waypoint nearest where it spawned.
						entity.WalksRoute = true;
						entity.RouteLoops = waypoints.Count > 2 && Vector3.Distance(waypoints[0], waypoints[^1]) <= RouteClosedTolerance;
						entity.RejoinRouteAtNearest();
						entity.PatrolBody = MoveTaskCodec.Build(new MoveTaskIIR(handle, entity.Position, MoveTaskNodes.Patrol(patrol)));
						// #714: a beat is WALKED, at the speed retail's own patrollers are observed moving at - not at the
						// chase speed, and not at the route's trailer scalar (which is not a ground speed; see
						// WorldState.RouteWalkSpeed for the measurement that rules it out).
						entity.RouteSpeed = WorldState.RouteWalkSpeed;
						entity.PatrolCycleSeconds = PatrolRoute.TotalLength(entity.Position, entity.PatrolWaypoints) / entity.RouteSpeed + PatrolDwellSeconds;
						entity.ArmPatrolCycle();
					}
				}

				// Disposition drives BOTH the overhead name colour and whether this NPC aggros. It is the captured
				// retail cCategory (NpcStatCore.Disposition, #256/aggro) - the SOLE correct hostility source. Community
				// is faction MEMBERSHIP, NOT hostility: community 1 is the civilian/default faction shared by Citizens,
				// vendors and guards, so the old `community > 0` test wrongly aggroed every townsperson. NULL disposition
				// -> friendly (the client's green default for an un-told-about NPC). Only cCategory 1 (hostile) aggros.
				entity.Disposition = npc.NpcTemplate.StatCore?.Disposition ?? WorldEntity.DispositionFriendly;
				entity.Hostile = entity.Disposition == WorldEntity.DispositionHostile;

				// The creature's death animation (#362): use the type's harvested death behaviorId, else the humanoid
				// default. Sent in the death BccIIR by CombatService.OnNpcKilled so demons/beasts play their own death.
				if (npc.NpcTemplate.DeathBehaviorId is { } deathBehavior)
					entity.DeathBehaviorId = deathBehavior;
				entity.DeathActivationTime = npc.NpcTemplate.DeathActivationTime;

				// The loot container this type drops on death (Bag by default, a bigger chest for bosses) - picked by
				// the kill-drop path (SpawnLootBagAt) via its LootContainerTemplate variant.
				entity.LootContainer = npc.NpcTemplate.LootContainerTemplateId;

				// Keep the NPC's FA015807 spawn body (addressed to the chosen handle) so interest/respawn can re-render
				// it for nearby players (#244): built WHOLE from typed columns (the captured Data blob was retired) with
				// this zone's load-time playfield token baked in. SendWorldSpawn re-stamps the entering player's LIVE
				// token per-send (SpawnTransformPatch), since that token isn't known until the player enters.
				// #363 dev label: the client ignores the spawn-body free `Name` for an NPC's floating nameplate (it uses
				// the template_id LDB name), so we DON'T bake the label into the spawn body - we push the AREA LABEL via a
				// SetNameIIR after the spawn (SendWorldSpawn), gated on the .togglenpcgroup toggle. The client hard-caps a
				// SetNameIIR name at 31 bytes, so we show the label alone (group | tier xN, ~24 chars) - the group name is
				// what a labeling pass needs. Stashed here unconditionally so the toggle is instant.
				if (AreaLabelFor(spawnAreas, npc) is { } areaLabel)
				{
					entity.OverheadNameOverride = areaLabel;
					labelledCount++;
					labelSample ??= areaLabel;
				}

				// #714: a creature that WALKS a captured beat spawns in movement mode 1, not the type's stored one.
				// The mode is what the client picks its locomotion speed by (PathMoveTask advances by a per-mode speed
				// field), and it is per-spawn STATE rather than type identity - retail sends 1 and 2 for the same
				// creature. Ours were locked to whatever value the type happened to be lifted with, which for the
				// Old Tarantia patrollers is 2, so they ran their beats.
				var spawnMovementMode = npc.PatrolNodes is { Count: > 0 } ? SpawnPostIirs.MovementModeOne : (uint?)null;
				entity.SpawnBody = NpcSpawnBuilder.BuildLive(npc, handle, _sim.World.ZoneFor(mapId, instanceId).PlayfieldToken, spawnMovementMode);

				// Caster NPCs (#232): the spells this NPC type casts, read from the #244 type FK (npc.NpcTemplate.Spells, loaded
				// with the spawn) - this replaces the fragile NpcName string join. Empty for non-casters.
				if (npc.NpcTemplate.Spells is { Count: > 0 } typeSpells)
				{
					foreach (var s in typeSpells)
						entity.Spells.Add(new NpcSpellCast(s.SpellId, NpcSpellEffectColumns.ToBuffData(s.SpellId, s.Duration, s.DescriptorSlot3, s.CastingLevel, s.FlagA, s.DescriptorSlot0, s.DescriptorSlot8)));
				}

				// Bark lines for this NPC type (per-NPC; empty for NPCs without a captured group -> silent). The entity
				// caches the bark VALUES; the chat tier renders them (#539), so the world model never holds pre-built
				// wire text - the same reason wave 6 took OriginalData off the snapshot sub-class.
				//
				// #694: split by CUE, not pooled. A bark's channel says what makes the NPC say it - ambient chatter runs
				// on the tick's clock, a `sendtell` greeting fires when a player is DETECTED arriving, and an `event`
				// line belongs to the encounter script's own trigger (#502). Pooling them put the crier's greeting and
				// the Damsel's cry for help on the ambient clock, which is why the crier greeted an empty street and the
				// Damsel called for help twice.
				if (npc.NpcTemplate.Chatter is { Count: > 0 } chatter)
				{
					var barks = chatter.Select(c => NpcChatterColumns.ToBark(c)).ToLookup(NpcChatterBarks.TriggerOf);
					entity.ChatterLines = barks[BarkTrigger.Ambient].ToArray();
					entity.GreetingLines = barks[BarkTrigger.Detection].ToArray();
					entity.EncounterLines = barks[BarkTrigger.EncounterScript].ToArray();
				}

				// #766: the ambient idle ANIMATIONS this creature performs while standing about, weighted as retail was
				// captured playing them. A type with none stands still - the honest gap, and the reason this is not
				// defaulted to the type's single `AnimIdleId` (that is the pose it holds, not something it does).
				if (npc.NpcTemplate.AmbientAnimations is { Count: > 0 } ambient)
				{
					entity.AmbientAnimations = ambient
						.Select(a => new AmbientAnimation(a.AnimationId, a.DurationCentiseconds, a.Weight))
						.ToArray();
					// Staggered, so a street's worth of citizens do not all start an animation on the same tick.
					entity.ArmAmbientAnimation(Random.Shared.NextDouble() * WorldState.AmbientAnimationPauseMedianSeconds * 4);
				}

				// #363: a captured spawn LINKED to an ambient area is a member of that area's POOL (the softer alternative
				// to graduating/deleting it). It renders with its captured appearance but the POOL owns its respawn (fades
				// on death, replaced by the pool), and it counts toward MaxCount so the pool only tops up the DEFICIT -
				// no captured/ambient overlap.
				if (npc.NpcSpawnAreaId is { } linkedAreaId)
				{
					entity.SpawnAreaId = linkedAreaId;
					entity.NoRespawn = true;
				}

				// #502: a placement whose TYPE has a scripted encounter on this map becomes a LURE - it watches for an
				// approaching player and swaps itself for its boss. Every placement of the type is armed, since the
				// definition is keyed by (map, type) rather than by one spot.
				ArmEncounter(entity, encounters.GetValueOrDefault(npc.NpcTemplateId));

				// #741: the creature TYPES this one walks with, kept until every entity is in the zone - a follower is
				// bound to a LIVE leader, so the binding cannot be made while the leader may not be loaded yet.
				if (npc.NpcTemplate.Follows is { Count: > 0 } follows)
					followers[entity] = follows.Select(f => f.LeaderTemplateId).ToHashSet();

				zone.Add(entity);
			}

			BindFormationFollowers(zone, followers);

			await LoadAmbientSpawns(mapId, instanceId, zone);

			_logger.LogInformation("World: loaded {Count} NPC(s) into map {Map} instance 0x{Instance:X8} ({Raw} DB rows, no runtime dedup).", zone.Count, mapId, instanceId, npcs.Length);
			_logger.LogDebug("World: #363 {Labelled}/{Total} captured spawns have a spawn-area label on map {Map} (.togglenpcgroup to show; sample: {Sample}).", labelledCount, npcs.Length, mapId,
				labelSample ?? "<none>");
		}

		// #262: whether the used world object is a quest-item PICKUP (a QuestItemEffect dynel - e.g. a quarry Stone
		// Brick), as opposed to a door/container. Only reports the object KIND; the pickup's EFFECT (grant item +
		// advance the CollectItem goal) is quest-driven, so the caller no-ops it when no collect quest is active - which
		// is what gates the brick nodes to Shipwrecked-V holders.
		public Task<bool> IsQuestPickup(INetworkClient client, uint handle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return Task.FromResult(false);

			return Task.FromResult(_sim.World.ZoneForEntity(player).TryGetWorldObject(handle, out var obj) && obj.Row.QuestItemEffect is > 0);
		}

		// #363 pool maintenance (called on a slow cadence from the sim tick): keep each loaded zone's ambient pools at
		// MaxCount. A killed member fades + is removed (LootExpired), dropping the pool's live count; here we respawn ONE
		// replacement per under-strength pool per pass (a gentle trickle - the cadence IS the respawn delay) at a free
		// point, streamed to nearby players.
		public async Task MaintainAmbientPools()
		{
			foreach (var ((mapId, instanceId), pools) in _ambientPools)
			{
				var zone = _sim.World.ZoneFor(mapId, instanceId);
				foreach (var pool in pools)
				{
					var live = zone.Entities.Where(e => e.SpawnAreaId == pool.AreaId && !e.IsDead).ToList();
					if (live.Count >= pool.MaxCount)
						continue;

					var pos = FreeAmbientPoint(pool, live);
					var entity = SpawnAmbientMember(pool, pos, zone);
					if (entity == null)
						continue;

					foreach (var viewer in zone.PlayersInRange(pos, WorldSimulation.PlayerInterestRange))
					{
						if (viewer.Client != null && viewer.Known.Add(entity.Handle))
							await SendWorldSpawn(viewer.Client, entity);
					}
				}
			}
		}

		// A player opened a world object (Interact UseObject). If it's a loot bag in their zone, OPEN ITS LOOT WINDOW
		// per the decoded flow (protocol_loot_interaction): assign the player as the current looter and send the
		// SimpleDynelVisualChange "Open" so the client shows the bag's contents. The bag STAYS in the world - it is
		// consumed on close (0x2044), not on open. Taking an item (0x2043 -> grant + LootRemoveItems) is the remaining,
		// in-client-gated piece. Returns false for non-loot / unknown objects so the caller can surface the gap.
		public async Task<bool> OpenLootBag(INetworkClient client, uint objectHandle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.ZoneForEntity(player).TryGet(objectHandle, out var bag) || !bag.IsLootable)
				return false;

			// NewCurrentLooter rides the WORLD conversation (GameAgent -> GameInterface), as in retail (mount/gameplay
			// capture frame 1477).
			await client.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(NewLooterCodec.Build(NewLooters.Current(bag.Handle, player.Handle)))));

			// The actual window-open is DispatchOpenLootBag (0x2037) on the GameCharAgent -> GameCharInterface
			// conversation (the same class-pair as the 0x201C spawn handshake / SpawnCheck), body [loot typetag][bag
			// handle] - NOT a SimpleDynelVisualChange on the world conversation (that only played the open animation, no
			// window). Decoded from the hoxxling gameplay capture (f1477): after this the client itself sends
			// TryLootCash (0x2045) then TryLootAll (0x2043), which we already handle.
			await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x58), client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchOpenLootBag,
				b => b.WriteUInt32(EntityClass.LootBag).WriteUInt32(bag.Handle)));

			_logger.LogInformation("Loot: {Name} opened the loot window on {Bag} 0x{Handle:X8}.", player.Name, bag.Name, bag.Handle);
			return true;
		}

		// A player used a STATIC world object (Interact UseObject) that isn't a loot bag - a DOOR/GATE (e.g. the
		// "King Conan's Throne Room" gate). Reply with the SimpleDynelVisualChange so the client drops the door's
		// collision and plays its open animation, matching the conans-castle capture (rec 1697 -> 1698).
		//
		// Both halves are now named rather than numbered (#678): `SetKdTree 0` is what removes the collision, and
		// "object_open" is a BASE ANIMATION name, not a state string. Retail's own close is the mirror image -
		// "object_closed" plus `SetKdTree` back to the object's `Kdtree` - which is why the collision id is the
		// object's own and not a constant. The object is resolved through the ZONE - the handle it was streamed with
		// is the zone's, not a column. Returns false if no such object exists, so the caller can fall through to the
		// loot-bag path.
		public async Task<bool> OpenWorldObject(INetworkClient client, uint handle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.ZoneForEntity(player).TryGetWorldObject(handle, out var live))
				return false;

			var obj = live.Row;
			var typeTag = WorldObjectColumns.TypeTagForClass(live.SubOpcode);

			await WorldIir.Send(client, SimpleDynelVisualChangeCodec.Build(SimpleDynelVisualChanges.SetKdTree(typeTag, handle, 0)));
			await WorldIir.Send(client, SimpleDynelVisualChangeCodec.Build(SimpleDynelVisualChanges.SetBaseAnim(typeTag, handle, "object_open")));
			_logger.LogInformation("Object: {Player} opened '{Obj}' 0x{Handle:X8}.", player.Name, obj.Name ?? WorldObjectDisplay.ToWire(obj) ?? "object", handle);
			return true;
		}

		// The resource NODE the player clicked, or null when the object is not one (#494). A query, so the listener can
		// own the grant the way it already owns the quest-item pickup's - see the UseObject handler.
		public LiveWorldObject? HarvestNodeAt(INetworkClient client, uint handle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.ZoneForEntity(player).TryGetWorldObject(handle, out var live))
				return null;

			return HarvestCatalog.IsHarvestable(live.Row.DisplayCategory, live.Row.DisplayId)
				? live
				: null;
		}

		// The achievement VISTA the player clicked, or null when the object is not one (#780). A query, like
		// HarvestNodeAt above, so the listener owns the send sequence and the tag award the way it already owns the
		// harvest grant.
		public LiveWorldObject? VistaAt(INetworkClient client, uint handle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.ZoneForEntity(player).TryGetWorldObject(handle, out var live))
				return null;

			return VistaCatalog.VistaFor(live.Row.DisplayCategory, live.Row.DisplayId) != null
				? live
				: null;
		}

		// Remove an EXHAUSTED harvest node from the live world for everyone who can see it, without touching its row.
		//
		// Deliberately NOT DeleteWorldObjectForPlayer: that deletes the row, which would make a harvested node gone for
		// good in every future session. The row stays and only this zone stops streaming it, so the node is back on the
		// next map load - the closest thing to retail's regrowth we can do without a measured respawn cadence.
		//
		// Wire handling mirrors DeleteWorldObjectForPlayer: a world object is quit by its OWN class word, not Character
		// (#366), and world objects aren't tracked in Known so the gate is "has this viewer entered the world".
		public async Task DespawnHarvestedNode(INetworkClient client, LiveWorldObject node)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return;

			var zone = _sim.World.ZoneForEntity(player);
			var classWord = WorldObjectColumns.TypeTagForClass(node.SubOpcode);
			foreach (var viewer in zone.PlayersInRange(node.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client.Session.WorldEntered)
					await SendWorldDespawn(viewer.Client, zone.PlayfieldToken, node.Handle, classWord);
			}

			zone.RemoveWorldObject(node.Handle);
		}

		// The quest-marker state byte for a handle the client polled via 0x2020 (resolves the entity in the player's
		// zone). 0x00 when it isn't a quest-giver in view. Lets the poll reply (SendSpawnCheckResponse) carry the
		// correct, state-driven marker instead of a hardcoded value.
		public async Task<byte> QuestMarkerStateForHandle(INetworkClient client, uint handle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player != null && _sim.World.ZoneForEntity(player).TryGet(handle, out var npc) && !npc.IsPlayer)
				return await ComputeGiverMarkerState(client, npc);

			return QuestMarkerNone;
		}

		// #241: re-send a quest-giver's marker to one viewer after their quest state changed (accept/complete) while
		// the giver is ALREADY in view. Retail updates it in-place, not only on stream-in (capture frame 698: Casilda
		// -> 0x00 the instant the quest is accepted). Resolves the entity in the player's zone, then recomputes.
		public async Task RefreshQuestGiverMarker(INetworkClient client, uint npcHandle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return;

			if (_sim.World.ZoneForEntity(player).TryGet(npcHandle, out var npc) && !npc.IsPlayer)
				await SendQuestMarkerIfGiver(client, npc);
		}

		// Resolves the playfield token a map's world-entry will stamp for a cross-map teleport's destination announce.
		// The map's real playfield-instance token (#305), threaded through map-change/spawn/world-objects/snapshot so
		// they agree by construction. Sourced per-map from Map.PlayfieldToken (the value the client scopes the
		// character to when it loads that playfield; the client keys per-area behaviour on it - e.g. mount permission,
		// so a wrong token makes Old Tarantia read as the no-mount Tortage beach). A missing MAP is a hard error and
		// throws; a found map with an un-captured token (0) falls back to the standard playfield token - that 0 is a
		// documented "no per-map override" state (most maps), not a lookup failure, so it is a real default rather than
		// a fallback masking a missing thing.
		public async Task<uint> ResolveMapPlayfieldToken(uint mapId)
		{
			var map = await _database.GetMap(mapId) ?? throw new InvalidOperationException($"ResolveMapPlayfieldToken: map {mapId} not found");
			return map.PlayfieldToken != 0
				? map.PlayfieldToken
				: WorldState.DefaultPlayfieldToken;
		}

		// Resolve a used world object's interaction, DATA-DRIVEN from its InteractAction + Destination* columns
		// (populated by the world-teleports importer from captured UseObject->N3Teleport sequences). A teleporter
		// WITHOUT a captured destination yields Teleport.DestMapId == 0 so the caller can surface a message. (Whether
		// it's a same-playfield Teleport or a cross-playfield ProxyTeleport, the handoff path is the same - DoTeleport
		// change-maps only when the destination map differs.)
		public Task<UsedObjectInteraction> ResolveUseObject(INetworkClient client, uint handle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.ZoneForEntity(player).TryGetWorldObject(handle, out var live))
				return Task.FromResult(new UsedObjectInteraction(InteractAction.None, default));

			var obj = live.Row;

			var name = obj.Name ?? WorldObjectDisplay.ToWire(obj) ?? "?";
			var teleport = obj.DestinationMapId is { } destMap
				? new ZoneLineDestination(destMap, obj.DestinationPosition, name)
				: new ZoneLineDestination(0, null, name);
			return Task.FromResult(new UsedObjectInteraction(obj.InteractAction, teleport, obj.Id));
		}

		// #398: the per-NPC follow-ups for the entry-burst NPCs (BuildEntryNpcBurst) - the quest markers and the
		// Casilda captive pose that DispatchInterest sends right after a streamed spawn. Sent AFTER the burst flush
		// (these are separate IIRs, not FA015807 spawn records) but still before the player starts moving.
		public async Task SendEntryNpcFollowups(INetworkClient client, List<WorldEntity> burstNpcs)
		{
			foreach (var npc in burstNpcs)
			{
				// #241: quest-giver marker from THIS viewer's quest state (! available / ? ready-to-turn-in / none).
				await SendQuestMarkerIfGiver(client, npc);

				// #267: a not-yet-freed Casilda streams in CHAINED (the captive struggle pose).
				if (npc.NpcTemplateId == QuestHandlerService.CasildaTemplateId && client.Session.Character != null && !await _questHandler.IsCasildaFreed(client.Session.Character.Id))
					await _questHandler.SendCasildaPose(client, npc.Handle, true);

				// #363 dev nameplate override (dev-only, gated on .togglenpcgroup).
				if (_debugAreaLabels && npc.OverheadNameOverride is { } overrideName)
					await SendSetName(client, npc.Handle, overrideName);
			}
		}

		// #262 companion-follow: make the NPC `npcHandle` follow `client`'s player (or stop, when follow=false). The
		// world tick (WorldState) then steps it toward the player and broadcasts a MoveTask each step. Returns the NPC,
		// or null if the handle isn't a non-player entity in the caller's zone. Used by the .npcfollow GM command and by
		// the Casilda rescue trigger.
		public WorldEntity? SetNpcFollow(INetworkClient client, uint npcHandle, bool follow)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.ZoneForEntity(player).TryGet(npcHandle, out var npc) || npc.IsPlayer)
				return null;

			npc.FollowTargetHandle = follow
				? player.Handle
				: null;
			_logger.LogInformation("Follow: {Name} 0x{Handle:X8} {State} {Player}.", npc.Name, npcHandle, follow
				? "now follows"
				: "stopped following", player.Name);
			return npc;
		}

		// Set the player's active weapon set explicitly (the .weaponset GM test-bootstrap forces a set; the live
		// SwitchWeaponsButton toggles via TogglePlayerWeaponSet). Persists + mirrors on the live entity; returns it.
		public async Task<WeaponSet> SetPlayerWeaponSet(INetworkClient client, WeaponSet set)
		{
			var player = _sim.PlayerWorldEntity(client);
			player?.ActiveWeaponSet = set;

			if (client.Session.Character is { } character)
			{
				character.ActiveWeaponSet = set;
				await _database.SaveCharacterWeaponSet(character.Id, set);
			}

			return set;
		}

		// Spawns a captured NPC (any NpcSpawns row with spawn Data, matched by NAME - the stable key) at the
		// player's position and renders it for nearby players. Built byte-exact via NpcSpawnBuilder (like the
		// map-load NPC spawns), re-framed + position-patched per recipient by SendWorldSpawn. When killable, the
		// entity gets vitals/level/hostility from its row exactly like EnsureZoneNpcs, so it joins combat + loot
		// as a full world citizen. Returns the spawned entity, or null when no spawnable row matches the name.
		public async Task<WorldEntity?> SpawnNpcForPlayer(INetworkClient client, string npcName, bool killable = true)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return null;

			var npc = await _database.GetNpcSpawnByName(npcName);
			if (npc == null)
			{
				_logger.LogInformation("Spawn: no spawnable '{NpcTemplate}' template found.", npcName);
				return null;
			}

			var handle = NextRuntimeHandle(); // a fresh summoned-entity handle (high range, no player/NPC collision)
			// Build wholly from columns (#243/#244); SendWorldSpawn re-stamps the position + token per recipient, so the
			// baked ones are just placeholders (the player's zone token here).
			var body = NpcSpawnBuilder.BuildLive(npc, handle, _sim.World.ZoneForEntity(player).PlayfieldToken);

			EntityVitals? vitals = null;
			if (killable)
			{
				var maxHealth = npc.Health > 0
					? npc.Health
					: CombatResolver.NpcMaxHealthForLevel(npc.Level);
				vitals = EntityVitals.FromMax(maxHealth, 0, maxHealth);
			}

			var entity = WorldEntity.ForNpc(handle, player.MapId, player.Position, player.Rotation, npc.NpcTemplate.Name ?? npcName, vitals, npc.NpcTemplateId);
			entity.InstanceId = player.InstanceId; // #268: spawn into the summoner's map instance (see SpawnPetForPlayer).
			entity.Level = npc.Level;
			entity.RangedAttacker = NpcWeapon.IsRanged(npc.Equipment); // #671, as in the zone-load path
			entity.MeleeComboId = await _combos.NpcMeleeComboId(npc.Equipment); // #623, likewise
			entity.ClassComboIds = await _combos.NpcClassComboIds(npc.Equipment, entity.Level); // #675, likewise
			entity.Disposition = npc.NpcTemplate.StatCore?.Disposition ?? WorldEntity.DispositionFriendly;
			entity.Hostile = killable && entity.Disposition == WorldEntity.DispositionHostile;
			entity.LootContainer = npc.NpcTemplate.LootContainerTemplateId; // drop this type's container on death
			entity.SpawnBody = body;
			entity.NoRespawn = true; // summoned test NPCs don't revive - they fade out after death

			// Load this NPC type's caster spells (same as the zone-load path) so a summoned caster actually casts
			// (#232) instead of only meleeing - the sim's cast branch lights up when entity.Spells is non-empty.
			if (entity.Name is { Length: > 0 } spellName)
			{
				foreach (var s in await _database.GetNpcSpellsByName(spellName))
					entity.Spells.Add(new NpcSpellCast(s.SpellId, NpcSpellEffectColumns.ToBuffData(s.SpellId, s.Duration, s.DescriptorSlot3, s.CastingLevel, s.FlagA, s.DescriptorSlot0, s.DescriptorSlot8)));
			}

			_sim.World.ZoneForEntity(player).Add(entity);

			foreach (var viewer in _sim.World.ZoneForEntity(player).PlayersInRange(player.Position, WorldSimulation.PlayerInterestRange))
			{
				// #307: register the spawn in the viewer's Known set. The follow/patrol MoveTask (BroadcastNpcMove) is
				// Known-gated (#245/#322/#323), and the client only RENDERS a spawned model once a MoveTask arrives at its
				// position - so a directly-spawned pet/NPC that isn't in Known sends its FA015807 but never its MoveTask,
				// and stays invisible. (This is why necro pets stopped rendering after the Known-gate was tightened.)
				if (viewer.Client != null && viewer.Known.Add(entity.Handle))
					await SendWorldSpawn(viewer.Client, entity);
			}

			_logger.LogInformation("Spawn: spawned {NpcTemplate} 0x{Handle:X8} for {Player} at {Pos}.", entity.Name, handle, player.Name, player.Position);
			return entity;
		}

		// Spawns a creature TYPE at the player as a PET (#307/#244) - the RIGHT model: no NpcSpawns world placement,
		// the FA015807 is assembled from the type's columns (StatCore + Equipment + Morphs) via BuildFromType at the
		// summoner's position, then streamed to nearby viewers. The PetIIR owner-assign + follow are done by the caller
		// (SummonPetForPlayer). BuildFromType throws if the type carries no lifted stat core (not yet imported/lifted).
		public async Task<WorldEntity?> SpawnPetForPlayer(INetworkClient client, NpcTemplate npcType)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return null;

			// BuildFromType throws for a type with no lifted StatCore.IirHeader - the fail-fast signal for un-lifted /
			// partially-imported data; it never happens for a shipped pet/mount type.
			var handle = NextRuntimeHandle();
			// Bake the summoner's zone token (4-byte) - see SpawnAmbientMember; a 0 token desyncs the client version read.
			var body = NpcSpawnBuilder.BuildFromType(npcType, handle, player.Position, player.Rotation, _sim.World.ZoneForEntity(player).PlayfieldToken);

			var level = npcType.StatCore?.Level ?? 0;
			var maxHealth = level > 0
				? CombatResolver.NpcMaxHealthForLevel(level)
				: 1;
			var vitals = EntityVitals.FromMax(maxHealth, 0, maxHealth);
			var entity = WorldEntity.ForNpc(handle, player.MapId, player.Position, player.Rotation, npcType.Name, vitals, npcType.TemplateId);
			entity.RangedAttacker = NpcWeapon.IsRanged(npcType.Equipment); // #671 - from the TYPE's equipment; a pet has no NpcSpawn row
			entity.MeleeComboId = await _combos.NpcMeleeComboId(npcType.Equipment); // #623, likewise
			entity.ClassComboIds = await _combos.NpcClassComboIds(npcType.Equipment, level); // #675, likewise
			entity.InstanceId = player.InstanceId; // #268: the pet belongs to the SUMMONER's map INSTANCE, not the default
			// (instance 0). Without this ZoneForEntity(pet) resolves a different zone/token
			// than the player's, so SendWorldSpawn re-stamps the wrong playfield token and the
			// client silently drops the spawn - no pet bar, no model. (Broke when #268 instanced.)
			entity.Level = level;
			entity.SpawnBody = body;
			entity.NoRespawn = true;

			foreach (var s in npcType.Spells)
				entity.Spells.Add(new NpcSpellCast(s.SpellId, NpcSpellEffectColumns.ToBuffData(s.SpellId, s.Duration, s.DescriptorSlot3, s.CastingLevel, s.FlagA, s.DescriptorSlot0, s.DescriptorSlot8)));

			_sim.World.ZoneForEntity(player).Add(entity);
			foreach (var viewer in _sim.World.ZoneForEntity(player).PlayersInRange(player.Position, WorldSimulation.PlayerInterestRange))
			{
				// #307: register the spawn in the viewer's Known set. The follow/patrol MoveTask (BroadcastNpcMove) is
				// Known-gated (#245/#322/#323), and the client only RENDERS a spawned model once a MoveTask arrives at its
				// position - so a directly-spawned pet/NPC that isn't in Known sends its FA015807 but never its MoveTask,
				// and stays invisible. (This is why necro pets stopped rendering after the Known-gate was tightened.)
				if (viewer.Client != null && viewer.Known.Add(entity.Handle))
					await SendWorldSpawn(viewer.Client, entity);
			}

			_logger.LogInformation("Pet: spawned creature type {Type} 0x{Handle:X8} for {Player}.", npcType.Name, handle, player.Name);
			return entity;
		}

		// FALLBACK standup, for a player who leaves the seat without asking (a teleport, or a client that skips the
		// request). The normal path is StandUp below, called when the client's own standup request arrives (behaviour 1,
		// phase 2) - inferring it from movement was our model, not retail's. No-op when not seated or still within
		// SeatLeaveDistance of the seat. Broadcasts the "standup" animation to the actor + nearby viewers and clears
		// the seated state.
		public async Task StandUpIfMoved(INetworkClient client, Vector3 newPosition)
		{
			if (client.Session.SeatedOnHandle is null)
				return;

			// Anchor to where the client actually PARKED the character. A bench has several seats (3 on the Old
			// Tarantia ones), so the sit animation snaps the player to a spot up to ~1.3 units from the position they
			// clicked at - measuring from the click point spends the whole budget on the snap, in an arbitrary
			// direction, and is why a real walk-off never crossed the threshold. The first move after sitting is the
			// parked position (retail's pkt 6812), so anchor there and measure from it.
			if (client.Session.SeatedPosition is not { } seat)
			{
				client.Session.SeatedPosition = newPosition;
				return;
			}

			if (Vector3.Distance(newPosition, seat) < SeatLeaveDistance)
				return;

			await StandUp(client);
		}

		// Send the standup animation and clear the seated state, unconditionally. This is the path retail actually uses:
		// getting up is CLIENT-INITIATED, not movement-inferred - see the behaviour-1 branch in the BCC handler.
		public async Task StandUp(INetworkClient client)
		{
			if (client.Session.SeatedOnHandle is not { } handle)
				return;

			client.Session.SeatedOnHandle = null;
			client.Session.SeatedHoldConfirmed = false;
			client.Session.SeatedPosition = null;
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return;

			var body = BehaviorControlCenterCodec.Build(BehaviorAnimations.PlayerStandUp(player.Handle, handle, BehaviorAnimations.StandRemoteformat()));
			await WorldIir.Send(client, body);
			await BroadcastBehaviorToViewers(client, body);
			_logger.LogInformation("Sit: {Player} stood up from 0x{Handle:X8}.", player.Name, handle);
		}

		// Take ONE item from a bag by its display POSITION - TryLootItem (0x2046), the single drag-out. The client does
		// NOT compact its loot window after a removal: it removes the looted slot, leaves a GAP, and keeps every survivor
		// at its ORIGINAL Pos - proven live (a 3-item bag dragged srcPos 1, then 2, then 0: the 2 could only arrive if
		// the survivors kept their original positions). So the take must be position-STABLE: null the slot (template 0 =
		// looted) in place rather than RemoveAt - shifting desynced the next drag's Pos and dropped the take. Returns the
		// template id removed, or null if the bag/Pos is invalid or already looted. (Loot-all uses TakeBagItems.)
		public uint? TakeBagItem(INetworkClient client, uint bagHandle, int index)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.ZoneForEntity(player).TryGet(bagHandle, out var bag) || !bag.IsLootable)
				return null;
			if (index < 0 || index >= bag.LootItems.Count || bag.LootItems[index] == 0)
				return null;

			var item = bag.LootItems[index];
			bag.LootItems[index] = 0; // looted-slot sentinel: survivors keep their stable Pos for the client's next drag
			_logger.LogInformation("Loot: {Player} loots item #{Index} (0x{Item:X}) from bag 0x{Handle:X8} ({Left} left).", player.Name, index, item, bagHandle, bag.LootItems.Count(i => i != 0));
			return item;
		}

		// A player clicked "loot all" (top-level 0x2043): hand the caller the bag's item templates to grant (via
		// DispatchAddItem) and clear them from the bag so each is granted exactly once. Empty when the bag is
		// unknown / not a loot bag / already emptied. Coins are a separate path (TryLootCash -> TakeLootCash).
		public IReadOnlyList<uint> TakeBagItems(INetworkClient client, uint bagHandle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.ZoneForEntity(player).TryGet(bagHandle, out var bag) || !bag.IsLootable || !bag.LootItems.Any(i => i != 0))
				return [];

			// Grant the still-present items (skip already-drag-looted slots, now 0); loot-all empties + closes the bag.
			var taken = bag.LootItems.Where(i => i != 0).ToArray();
			bag.LootItems.Clear();
			_logger.LogInformation("Loot: {Player} loots {Count} item(s) from bag 0x{Handle:X8}.", player.Name, taken.Length, bagHandle);
			return taken;
		}

		// Puts an item BACK on the bag after a take whose grant failed (a full backpack, or an item the snapshot cannot
		// build). Without it the take is destructive: the bag hands each item over exactly once, so a dropped grant
		// deletes the loot - and the bag is emptied BEFORE the grant is attempted, so this cannot be prevented by
		// checking first without duplicating the capacity rules the grant already owns. `position` restores a
		// single-drag take to its original slot (the client keeps survivors at their original Pos - see TakeBagItem);
		// loot-all appends, having cleared the list.
		public void ReturnBagItem(INetworkClient client, uint bagHandle, uint itemTemplateId, int? position = null)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.ZoneForEntity(player).TryGet(bagHandle, out var bag))
				return;

			if (position is { } pos and >= 0 && pos < bag.LootItems.Count)
				bag.LootItems[pos] = itemTemplateId;
			else
				bag.LootItems.Add(itemTemplateId);

			_logger.LogWarning("Loot: item 0x{Item:X} could not be granted to {Player}; returned to bag 0x{Handle:X8}.", itemTemplateId, player.Name, bagHandle);
		}

		// TryLootCash (0x2045): takes the bag's coins exactly once - returns the amount and zeroes it on the bag
		// (the caller credits the wallet + pushes the 0x2005 balance update). 0 = no bag / no coins / already taken.
		public uint TakeLootCash(INetworkClient client, uint bagHandle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.ZoneForEntity(player).TryGet(bagHandle, out var bag) || !bag.IsLootable)
				return 0;

			var cash = bag.CashTin;
			bag.CashTin = 0;
			return cash;
		}

		// #363 `.togglenpcgroup`: flip the dev spawn-area nameplate labels for the WHOLE server, then immediately
		// re-apply to every labelled NPC the caller already knows in their zone - pushing the area label when turning ON,
		// or restoring the plain creature name (which matches the client's LDB name) when turning OFF - so the toggle is
		// visible without a re-stream. NPCs that stream in later pick it up in SendWorldSpawn. Returns (nowOn, affected).
		public async Task<(bool On, int Affected)> ToggleAreaLabels(INetworkClient client)
		{
			_debugAreaLabels = !_debugAreaLabels;
			var player = _sim.PlayerWorldEntity(client);
			var affected = 0;
			if (player != null)
			{
				foreach (var npc in _sim.World.ZoneForEntity(player).Entities)
				{
					if (npc.OverheadNameOverride is not { } label || !player.Known.Contains(npc.Handle))
						continue;

					await SendSetName(client, npc.Handle, _debugAreaLabels
						? label
						: npc.Name ?? "");
					affected++;
				}
			}

			return (_debugAreaLabels, affected);
		}

		// #400 weapon-set switch: flip the player's active weapon LOADOUT (Primary<->Secondary), persist it so the drawn
		// set + the login-time gear-stat aggregation (which counts only the active set's weapons) survive relog, and
		// mirror it on the live entity. Returns the now-active set. The SwitchWeaponsButton action (0x2E) is the SET
		// switch, not a draw/sheathe (capture-confirmed: the acting player's newly-active weapon main slot alternates
		// 6<->20). No-op-safe: defaults Primary->Secondary if the player isn't in the world yet.
		public Task<WeaponSet> TogglePlayerWeaponSet(INetworkClient client)
		{
			var current = _sim.PlayerWorldEntity(client)?.ActiveWeaponSet ?? client.Session.Character?.ActiveWeaponSet ?? WeaponSet.Primary;
			return SetPlayerWeaponSet(client, current == WeaponSet.Primary
				? WeaponSet.Secondary
				: WeaponSet.Primary);
		}

		// Seat the player on a BENCH (the sit interaction - tools/capture/decode_sit.py). Interacting with a bench
		// plays the actor's "chair_sit_calm" animation (a server-originated BehaviorControlCenterIIR) and marks the
		// session seated; standing back up is triggered by the next move (StandUpIfSeated). Recognized by the object
		// NAME ("Bench"), the same stable key as shop vendors / the auctioneer. Returns false when the used object is
		// NOT a bench, so the caller falls through to the normal open-object / loot path. Renders for the actor and
		// every nearby player who already sees them.
		public async Task<bool> TrySitOnObject(INetworkClient client, uint handle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return false;

			if (!_sim.World.ZoneForEntity(player).TryGetWorldObject(handle, out var live) || !string.Equals(live.Row.Name, "Bench", StringComparison.OrdinalIgnoreCase))
				return false;

			var obj = live.Row;

			// The sit is a TWO-phase handshake (#355, capture sit-bench frames 6805-6813). The client sends the bench
			// UseObject TWICE: request #1 BEFORE it has the pose, request #2 AFTER it receives the animation. Retail
			// answers EACH - request #1 with the hold-echo (0x12 seq 0x0004) + the sit animation (0x2A), request #2 with
			// the hold-assert (0x12 seq 0x000F) that COMMITS the pose. Answering only request #1 (our old behavior: send
			// all three up-front, then silently absorb the duplicate) leaves the client's second confirm unanswered, so
			// the sit-down plays and then reverts after the ~2.6s chair_sit_calm blend. Split it: seat + echo + animate
			// on the first, assert once on the second, absorb any further duplicates.
			if (client.Session.SeatedOnHandle == handle)
			{
				// Phase 2 - the client's confirming re-send. Commit the pose with the hold-assert, exactly once.
				if (!client.Session.SeatedHoldConfirmed)
				{
					var assert = BehaviorControlCenterCodec.Build(BehaviorAnimations.BenchSitState(player.Handle, BehaviorAnimations.SeqObjectHold, handle));
					await WorldIir.Send(client, assert);
					await BroadcastBehaviorToViewers(client, assert);
					client.Session.SeatedHoldConfirmed = true;
					_logger.LogInformation("Sit: {Player} hold-confirmed on 0x{Handle:X8}.", player.Name, handle);
				}

				return true;
			}

			// Phase 1 - the initial UseObject. Retail brackets the sit ANIMATION with the object-action hold STATE
			// (behaviorId 0x12): the hold-echo BEFORE the animation, then the animation itself. The hold-assert is NOT
			// sent yet - it answers the client's phase-2 re-send above.
			var sit = BehaviorControlCenterCodec.Build(BehaviorAnimations.PlayerSitOnObject(player.Handle, handle, BehaviorAnimations.SitRemoteformat(BenchObjectClass)));
			var holdEcho = BehaviorControlCenterCodec.Build(BehaviorAnimations.BenchSitState(player.Handle, BehaviorAnimations.SeqBegin, handle));

			foreach (var body in new[] { holdEcho, sit })
			{
				await WorldIir.Send(client, body);
				await BroadcastBehaviorToViewers(client, body);
			}

			client.Session.SeatedOnHandle = handle;
			client.Session.SeatedPosition = null; // anchored from the parked spot on the first move after sitting
			client.Session.SeatedHoldConfirmed = false;
			_logger.LogInformation("Sit: {Player} sat on '{Obj}' 0x{Handle:X8}.", player.Name, obj.Name, handle);
			return true;
		}

		// The QUEST ITEM the killer needs off `npcName`'s corpse - the item id of their active LootFromNpc goal targeting
		// it (#261), or null when they have no such goal. A non-null result forces the bag drop (the item must be
		// lootable, not a 50/50 roll) and names what to put in it. Both goals in the game data want an actual key:
		// Saddur -> 193186 "Acheronian Gate Key", Scavenger Tuut -> 319224 "Scavenger's Key", each item type 113 (Quest).
		private async Task<uint?> ActiveLootGoalItemFor(uint characterId, string npcName, uint npcTemplateId = 0)
		{
			if (string.IsNullOrEmpty(npcName) && npcTemplateId == 0)
				return null;

			// One batched read rather than a query per active quest - this sits on the LOOT path, so it ran per kill
			// that could drop a bag.
			foreach (var (quest, def) in await _database.GetCharacterQuestsWithDefinitions(characterId, QuestState.Active))
			{
				// #521: template first, name second - the same test MatchingLootGoal applies when the item is actually
				// taken. The two must agree, or the drop is forced for a goal the take then refuses to advance.
				var goal = QuestProgress.InPlayOrder(def.Goals).FirstOrDefault();
				if (goal is not { GoalType: QuestGoalType.LootFromNpc } || !QuestProgress.TargetsNpc(goal, npcTemplateId, npcName))
					continue;

				// A goal that names no item cannot be satisfied: the objective is RECEIVING the item, and there is none
				// to receive or to match a take against (QuestProgress.MatchingLootGoal). Forcing a bag drop for it just
				// produced an empty bag that looked lootable and advanced nothing. Every LootFromNpc goal in the data
				// names its item, so this is a gap in the imported quest rather than a shape to support.
				if (goal.QuestItemId is not { } questItemId || questItemId == 0)
				{
					_logger.LogError("Quest {Quest} goal {Goal} loots '{Npc}' but names no QuestItemId, so it can never complete - the goal needs its item harvested.", quest.QuestId, goal.GoalId,
						npcName);
					return null;
				}

				return questItemId;
			}

			return null;
		}

		// The quest-marker state byte (#241) for a viewer + a giver NPC in view, from the player's CharacterQuests:
		// no quest held -> '!' (0x02); goals complete -> '?' (0x04); active/in-progress -> cleared (0x00). Returns
		// 0x00 for a non-giver. Shared by the interest-spawn push AND the 0x2020-poll reply so they never disagree.
		//
		// It takes the ENTITY rather than a name because its two questions want different keys (#646): "is this the NPC
		// my next talk goal names" is name-vs-name, since a talk goal's target is a string in the client's own RDB data
		// (#521), while "does this NPC give a quest" is answered by the TEMPLATE, which is what the dialogue graph the
		// giver map is derived from is keyed by.
		private async Task<byte> ComputeGiverMarkerState(INetworkClient client, WorldEntity npc)
		{
			var character = client.Session.Character;
			var npcName = npc?.Name;
			if (npc == null || npcName == null || character == null)
				return QuestMarkerNone;

			// '?' (0x04) when the player has an ACTIVE quest whose current goal is to TALK to this NPC - she's the
			// next step (e.g. the Casilda chain's talk-to-free stage 13480, active after looting Tuut's key). This is a
			// SEPARATE source from the giver logic below: the giver quest (13479) is already Completed/superseded by
			// then, so without this Casilda showed no '?'. Decoded from the captive-quest capture: retail sends 0x2008
			// state 0x04 to her handle at the loot->talk transition (Blablatest fr1455).
			// ⚠️ This runs once per 0x2020 spawn poll, and the client polls once per NPC it is loading - ~77 of them on
			// the Tortage beach. A query per ACTIVE quest here made each poll ~12 DB round trips / ~257ms, so the
			// client's serialized poll queue took ~20s to drain and every request behind it - including its 5s latency
			// check - measured that backlog. One batched read instead; order is preserved, so the FIRST match wins.
			// The giver logic below needs the raw rows in EVERY state (held / completed), so that stays a plain read.
			var quests = await _database.GetCharacterQuests(character.Id);
			foreach (var (_, qDef) in await _database.GetCharacterQuestsWithDefinitions(character.Id, QuestState.Active))
			{
				// #521: template first, name second - the same test the talk TRIGGER uses, so the marker and what
				// actually fires on the greeting cannot disagree.
				var qGoal = QuestProgress.InPlayOrder(qDef.Goals).FirstOrDefault();
				if (qGoal?.GoalType == QuestGoalType.TalkToNpc && QuestProgress.TargetsNpc(qGoal, npc.NpcTemplateId, npcName))
					return QuestMarkerTurnIn;
			}

			_questGivers ??= await _database.GetQuestGiverMap();
			if (!_questGivers.TryGetValue(npc.NpcTemplateId, out var questId))
				return QuestMarkerNone;

			// A giver's '!'/'?' belongs ONLY to the NPC on the grant-quest's own map. Gate on the grant quest's MapId
			// (its QuestDefinition, #244): only the giver's own map shows it. This used to also be what stopped a
			// same-NAMED NPC world-wide from wearing the marker - "Halen van Dummy" is the internal name of 27
			// unrelated templates - but the lookup above is keyed by TEMPLATE since #646, so the map gate is back to
			// meaning only what it says.
			var giverQuest = await _database.GetQuestDefinition(questId);
			if (giverQuest != null && giverQuest.MapId != npc.MapId)
				return QuestMarkerNone;

			var held = Array.Find(quests, q => q.QuestId == questId);
			// '?' once the objective is done but the quest is still ACTIVE (awaiting turn-in at the giver - a quest is
			// only marked Completed on turn-in, not on the kill, #47/#241). Otherwise, held or not, the choice is
			// between the GOLD '!' and the SILVER one, and it is the same question both times: does this NPC have
			// something for you RIGHT NOW (#559)?
			//   * not held, and every earlier step of its line is done -> GOLD, it is yours to take;
			//   * not held, but the line has not reached it -> SILVER. This is Turach at login in
			//     `xxx_mage-necromancer_login_play2_tortage_city_first-quests`: his "The Quarry" is step 5 of line 241
			//     and the player has done none of steps 1-4, so retail greys him rather than drawing no marker;
			//   * held and in progress, or turned in -> SILVER, he is still part of the chain with nothing to give.
			if (held is { State: QuestState.Active, GoalRequired: > 0 } && held.GoalProgress >= held.GoalRequired)
				return QuestMarkerTurnIn;

			if (held != null)
				return QuestMarkerUnavailable;

			return giverQuest != null && !await IsLineReady(giverQuest, quests)
				? QuestMarkerUnavailable
				: QuestMarkerAvailable;
		}

		// #559: whether `quest`'s line has actually reached it - every EARLIER step of the same quest line completed.
		// This is what separates the gold '!' from the silver one for a quest the player does not hold: retail greys a
		// giver whose quest exists but is not yet reachable, rather than drawing no marker.
		//
		// ⚠️ JournalSequence 0 is a legitimate captured value, not "unordered" - 74 captured quests carry it and 182
		// lines have it on every step (see GetQuestDefinitionsByLine). A line ordered entirely by 0s cannot say what
		// comes first, so a 0 on either side is skipped rather than guessed at: an unorderable line stays GOLD, which is
		// the behaviour before this gate existed. Only a genuine lower sequence gates.
		private async Task<bool> IsLineReady(QuestDefinition quest, CharacterQuest[] quests)
		{
			return quest.JournalSequence == 0 || LineReached(quest, await _database.GetQuestDefinitionsByLine(quest.QuestLineId), quests);
		}

		// The rule itself, split from the fetch so it can be tested against the real quest lines without a broadcaster.
		internal static bool LineReached(QuestDefinition quest, IEnumerable<QuestDefinition> line, CharacterQuest[] quests)
		{
			foreach (var step in line)
			{
				if (step.JournalSequence == 0 || step.JournalSequence >= quest.JournalSequence)
					continue;

				if (Array.Find(quests, q => q.QuestId == step.QuestId) is not { State: QuestState.Completed })
					return false;
			}

			return true;
		}

		// Dispatches interest events. `move` (the mover's Move components) is required to forward Move events and is
		// null on entry (entry produces only Spawn/Despawn).
		// Dispatches an entity interest pass AND, for the moving/refreshing player, the static world-object pass. The two
		// are fused into one call on purpose: they are the same concern (bring this player's view in line with what is
		// near them) but obey different known-set rules, and when they were two separate calls at two call sites I wired
		// the object half into the quest-visibility path only - the two methods have identical three-line bodies, so a
		// textual edit hit the wrong one and objects never streamed as the player walked. Taking the player here means a
		// caller cannot dispatch one half without the other.
		private async Task DispatchInterest(IReadOnlyList<InterestEvent> events, byte[]? moveBody, INetworkClient? mover = null, WorldEntity? moverEntity = null)
		{
			if (mover != null && moverEntity != null)
				await StreamWorldObjects(mover, moverEntity);

			foreach (var ev in events)
			{
				var recipient = ev.Recipient.Client;
				if (recipient == null)
					continue;

				switch (ev.Type)
				{
					// A player is spawnable when it has a live entity (its record is built per send); everything else
					// still needs its prepared body.
					case InterestEventType.Spawn when ev.Subject.IsPlayer || ev.Subject.SpawnBody != null:
					{
						// Register a streamed-in NPC's handle -> name + template so clicking it routes to the right
						// conversation/shop by the stable template key (the same map the world-burst spawns use; players
						// are not conversation targets).
						if (ev.Subject is { IsPlayer: false, IsLoot: false })
						{
							recipient.Session.SpawnedNpcNamesByHandle[ev.Subject.Handle] = ev.Subject.Name;
							recipient.Session.SpawnedNpcTemplatesByHandle[ev.Subject.Handle] = ev.Subject.NpcTemplateId;
						}

						await SendWorldSpawn(recipient, ev.Subject);

						// A MOUNTED player's spawn record cannot show the mount: SpawnBody is the FA015807 captured at
						// their world entry, frozen before they ever mounted. The client renders the horse from the
						// Mount/MountSpeed stats, so send those straight after the spawn - otherwise walking up to a
						// mounted player shows them on foot, which is the half of #428 the live relay does not cover.
						if (ev.Subject is { IsPlayer: true, IsMounted: true, MountRideStat: > 0 })
						{
							await recipient.SendAsync(GameServerListener.BuildStatUpdate(recipient, new CharacterStatBlock
							{
								[CharacterStatId.Mount] = ev.Subject.MountRideStat,
								[CharacterStatId.MountSpeed] = 100
							}, ev.Subject.Handle));
						}

						// #241: when a quest-giver NPC enters view, set its marker from THIS viewer's quest state
						// (! available / ? ready-to-turn-in / none active). Recomputed per entry, so it transitions as
						// the player accepts/completes (e.g. Casilda shows ! then ? after the quest is done).
						if (ev.Subject is { IsPlayer: false, IsLoot: false })
							await SendQuestMarkerIfGiver(recipient, ev.Subject);
						// #716: a ROUTE walker's beat is issued here so the CLIENT walks it. This was removed once, for a
						// good reason that no longer holds: the client-walked route left entity.Position pinned at spawn,
						// so the server put the NPC's death-pose, loot bag and aggro where it STARTED rather than where the
						// player saw it. The model now walks the same polyline itself (WorldState.WalkRoute), at the route's
						// own captured speed, so it tracks the render without a packet per 6u - which is what made the beat
						// cost 19.3 MoveTask/s against retail's 3.09/s corpus maximum, and made the NPC run.
						//
						// ⚠️ Known gap: the body is anchored at the entity's SPAWN, so a viewer arriving MID-lap is told to
						// start the walk from the beginning. Retail's own form carries [duration][remaining] (535 of 759
						// captured multi-point patrol nodes do), which is presumably how it hands a late viewer the REST of
						// the beat; reproducing that is the rest of #716.
						// ⚠️ ARMED, not sent. Retail lets the client settle the spawn before handing it a timed walk (p50
						// 4.02s between the two over 2,519 captured NPCs; only 3% inside 0.1s). Sending both in the same
						// instant is what made a freshly-spawned NPC dash its first laps. The tick fires the beat to
						// everyone in range once the delay elapses - see WorldState.RoutePatrolDelaySeconds.
						if (ev.Subject is { IsPlayer: false, WalksRoute: true })
							ev.Subject.RoutePatrolDelayRemaining = WorldState.RoutePatrolDelaySeconds;
						// #267: a not-yet-freed Casilda streams in CHAINED (the captive struggle pose); once the player has
						// freed her she streams in normally (and follows). Per-viewer + per-instance (the beach is Solo).
						if (ev.Subject is { IsPlayer: false, NpcTemplateId: QuestHandlerService.CasildaTemplateId } && recipient.Session.Character != null &&
						    !await _questHandler.IsCasildaFreed(recipient.Session.Character.Id))
							await _questHandler.SendCasildaPose(recipient, ev.Subject.Handle, true);
						break;
					}

					case InterestEventType.Move when moveBody != null:
					{
						await SendWorldMove(recipient, moveBody);
						break;
					}

					case InterestEventType.Despawn:
					{
						await SendWorldDespawn(recipient, _sim.World.ZoneForEntity(ev.Recipient).PlayfieldToken, ev.Subject.Handle);
						break;
					}
				}
			}
		}

		// #261: reload the player entity's ActiveQuestIds from the DB (the set interest gating tests gated NPCs against).
		private async Task LoadActiveQuestIds(WorldEntity entity)
		{
			var characterId = entity.Client.Session.Character?.Id;
			if (characterId == null)
				return;

			var quests = await _database.GetCharacterQuests(characterId.Value);
			entity!.ActiveQuestIds.Clear();
			entity.CompletedQuestIds.Clear();
			foreach (var quest in quests)
			{
				if (quest.State == QuestState.Active)
					entity.ActiveQuestIds.Add(quest.QuestId);
				else if (quest.State == QuestState.Completed)
					entity.CompletedQuestIds.Add(quest.QuestId); // #262: drives "hide once done" NPC gating
			}
		}

		// #363 live ambient spawner: the capture-INDEPENDENT open-world
		// population, managed as spawn-group POOLS. Each RESOLVED NpcSpawnArea becomes an AmbientPool that keeps ~MaxCount
		// of its creature alive within the group's area: the initial fill spawns one per RDB point, each member roams the
		// group's points (its PatrolWaypoints -> the #266 wander) and fades on death (NoRespawn); MaintainAmbientPools
		// then respawns the deficit at a free point. Built WHOLE from the creature TYPE (TryBuildFromType, the pet path).
		// Worked example - Tortage beach (4010): the four T_Crocodile-* areas become four Crocodile pools (8/7/8/11)
		// that each hold that many crocs roaming their stretch of shore, refilled as the player kills them.
		// #741: bind each formation FOLLOWER to the nearest live leader of a type it walks with. Retail expresses this
		// as a MoveTaskIIR whose task names the other creature's handle, so it is a per-PLACEMENT binding built out of
		// a per-TYPE rule - which is exactly what the DB holds (NpcFollowPair) and what cannot be resolved until every
		// entity is in the zone.
		//
		// NEAREST, and within FormationBindRange: a map carries several groups of the same two types (five separate
		// `Nemedian Deathstalker` groups in one capture, four `Ginger`/`Scruffy` pairs across three), so binding to
		// "any leader of that type" would march a follower across the zone to the wrong one. The range is ours, not
		// retail's - the corpus states the pairing, never a maximum distance - so it is set at the leash territory a
		// patroller already works in rather than invented smaller.
		//
		// A leader that is itself a follower is fine (nothing in the corpus chains, but nothing forbids it); a
		// follower that finds no leader in range simply keeps its own behaviour.
		// How far a formation follower will look for its leader at zone load. OURS, not retail's - the corpus states
		// which creature walks with which and never a maximum distance - so it is the leash territory a patroller
		// already works in rather than a smaller invented number.
		private const float FormationBindRange = WorldState.NpcLeashRange;

		private void BindFormationFollowers(Zone zone, Dictionary<WorldEntity, HashSet<uint>> followers)
		{
			if (followers.Count == 0)
				return;

			var bound = 0;
			foreach (var (follower, leaderTemplates) in followers)
			{
				WorldEntity? nearest = null;
				var nearestDist = FormationBindRange * FormationBindRange;
				foreach (var candidate in zone.Entities)
				{
					if (candidate == follower || candidate.IsPlayer || !leaderTemplates.Contains(candidate.NpcTemplateId))
						continue;

					var dx = candidate.Position.X - follower.Position.X;
					var dz = candidate.Position.Z - follower.Position.Z;
					var dist = dx * dx + dz * dz;
					if (dist >= nearestDist)
						continue;

					nearestDist = dist;
					nearest = candidate;
				}

				if (nearest == null)
					continue;

				follower.FollowTargetHandle = nearest.Handle;
				bound++;
			}

			_logger.LogDebug("World: #741 bound {Bound} of {Total} formation follower(s) to a leader within {Range}u.", bound, followers.Count, FormationBindRange);
		}

		private async Task LoadAmbientSpawns(uint mapId, uint instanceId, Zone zone)
		{
			var areas = await _database.GetResolvedSpawnAreas(mapId);
			var pools = new List<AmbientPool>();
			var spawned = 0;
			foreach (var area in areas)
			{
				var type = area.NpcTemplate;
				if (type?.StatCore?.IirHeader == null)
					continue; // the resolved creature has no buildable stat core - skip (can't construct its body)

				var points = area.Points.Select(p => p.Position).ToArray();
				if (points.Length == 0)
					continue;

				var level = type.StatCore.Level ?? 0;
				var pool = new AmbientPool(area.Id, area.MaxCount, points, type, level, level > 0
					? CombatResolver.NpcMaxHealthForLevel(level)
					: 1u, type.StatCore.Disposition ?? WorldEntity.DispositionFriendly, FormatAreaLabel(area));
				pools.Add(pool);

				// The captured spawns LINKED to this area (tagged in the load loop above) are already in the zone as pool
				// members - the "obsolete npcspawns" that fill the pool first; only spawn the DEFICIT up to MaxCount, at
				// points not already occupied. So a resolved area with captured members doesn't double-populate.
				var members = zone.Entities.Where(e => e.SpawnAreaId == area.Id).ToList();
				for (var i = members.Count; i < pool.MaxCount; i++)
				{
					var member = SpawnAmbientMember(pool, FreeAmbientPoint(pool, members), zone);
					if (member == null)
						break;

					members.Add(member);
					spawned++;
				}
			}

			_ambientPools[(mapId, instanceId)] = pools;
			if (spawned > 0)
				_logger.LogInformation("World: +{Count} ambient NPC(s) across {Pools} spawn-area pool(s) on map {Map}.", spawned, pools.Count, mapId);
		}

		private uint NextRuntimeHandle()
		{
			return (uint)Interlocked.Increment(ref _nextRuntimeHandle);
		}

		// A few random snapshot-buildable item templates for a loot bag. Drops from the MAP's loot pool
		// (#273) - the items that map's captured bags actually carried - falling back to the all-items random sample when
		// the map has no captured pool (so a map we have no loot data for still drops something). Empty when count <= 0.
		private async Task<List<uint>> RollLootItems(int count, uint mapId)
		{
			if (count <= 0)
				return [];

			var items = await _database.GetMapLootSample(mapId, count);
			if (items.Length == 0)
				items = await _database.GetRandomBuildableItems(count); // map has no captured loot pool - fall back

			return items.Select(i => i.Id).ToList();
		}

		// #241: drive a quest-giver NPC's marker from the viewer's quest state when it enters view. Recomputed each
		// time the NPC is streamed in, so the '!' transitions to '?' (and clears) as the player accepts/completes the
		// quest - e.g. Casilda shows '!' before, nothing while held, '?' once the kill goal is done (return to turn in).
		private async Task SendQuestMarkerIfGiver(INetworkClient client, WorldEntity npc)
		{
			if (npc.Name == null || client.Session.Character == null)
				return;

			_questGivers ??= await _database.GetQuestGiverMap();
			if (!_questGivers.ContainsKey(npc.NpcTemplateId))
				return; // not a quest-giver -> no marker

			await SendQuestMarker(client, npc.Handle, await ComputeGiverMarkerState(client, npc));
		}

		// Removes the subject from `recipient`'s view - the n3ToClientQuit despawn (N3ToClientQuitCodec). Stamped with the
		// recipient's INSTANCE playfield token (#268; ZoneForEntity(recipient).PlayfieldToken at the call site): the
		// burst's value for a replayed playfield (what the client learned at entry), else WorldState's minted fallback.
		// The token-is-0 guard is just defensive (ZoneFor always mints one).
		private Task SendWorldDespawn(INetworkClient recipient, uint playfieldToken, uint subjectHandle, uint classWord = EntityClass.Character)
		{
			var token = playfieldToken;
			if (token == 0)
			{
				_logger.LogDebug("World: skip despawn {Subject:X8} for a viewer (playfield token unknown).", subjectHandle);
				return Task.CompletedTask;
			}

			return recipient.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, recipient.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(N3ToClientQuitCodec.Build(subjectHandle, token, classWord))));
		}

		private async Task SendWorldSpawn(INetworkClient recipient, WorldEntity subject)
		{
			var playfieldToken = _sim.World.ZoneForEntity(subject).PlayfieldToken;

			// A PLAYER's record is REBUILT from live state, not spliced out of a cached one (#429). The cached body is
			// the FA015807 from that player's own world ENTRY, so a viewer arriving later was handed a description of
			// them as they were at LOGIN - the mount was simply the first thing anyone noticed missing; equipment,
			// level and vanity have the same hole. Nothing here is replayed: PlayerCharacterIirs builds the message
			// from code (#258) and PlayerSpawnBuilder builds the record from scratch, so calling them per send costs
			// one equipment load and always describes the player as they are NOW.
			if (subject.IsPlayer)
			{
				var live = await BuildPlayerSpawnBody(subject, playfieldToken);
				if (live == null)
				{
					// No live session to describe. Loudly, not silently: a player we cannot build is a player nobody can
					// see, and the old code would have papered over it with a stale cached body.
					_logger.LogWarning("Spawn: cannot build a live record for player 0x{Handle:X8} ({Name}) - not sent.", subject.Handle, subject.Name);
					return;
				}

				await recipient.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, recipient.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
					s => s.WriteArrayUInt32Length(live)));
			}
			else
			{
				await recipient.SendAsync(BuildWorldSpawnRecord(recipient, subject, playfieldToken));
			}

			// #363 dev nameplate: the client draws an NPC's floating name from its template_id LDB name and ignores the
			// spawn-body `Name`, so overriding the visible name needs a SetNameIIR (0xADCE0CDA). Sent right AFTER the
			// spawn (in TCP order) so the entity + its OverheadText exist before the name lands. Dev-only, gated on the
			// live `.togglenpcgroup` toggle, so it never touches retail parity.
			if (_debugAreaLabels && subject.OverheadNameOverride is { } overrideName)
				await SendSetName(recipient, subject.Handle, overrideName);

			await SendActiveBuffsOnStreamIn(recipient, subject);
		}

		// #677: an entity STREAMING INTO VIEW mid-buff. The observer arrived after the cast, so they have no apply
		// event to count from and see no icon - and re-sending the apply is exactly what retail's absolute expiry
		// stamp is FOR.
		//
		// ⭐ The shape is measured, not invented (`--report buff-on-spawn`). Over 17,717 captured spawn bodies and
		// 2,929 applies there is exactly ONE such record, and it is unique in three independent ways at once, which
		// is what makes one sighting enough to build from:
		//
		//   NecroL80_..._login_spellweaving +135.5s   to 0x0215E80C (a CHARACTER), spawned 0.00s earlier
		//   `Slayer of the Leviathus Spawn`  18,735,004 cs of 276,480,000 cs (6.8%)
		//   ApplyFrom = 0        <- a CHARACTER reference whose handle is zero: "a caster, unspecified"
		//   descriptor[3] = 0    <- no caster named, so no caster level
		//   CastingLevel = 80    <- the TARGET's level, which is the #658 decode falling out for free
		//
		// ⚠️ That zero-handle caster is NOT the same thing as the 11 casterless applies `ApplyNoCaster` was written
		// for. Those write a zero CLASS word and no reference at all (the Racer buff, #636); this one writes the
		// Character class and a zero instance behind it. The 54-byte data region is identical between the two forms -
		// which is why the payload factory is shared - but the carrier is not, so `ApplyFrom` is `0u` here and `null`
		// there. Passing `null` would build a record retail does not send.
		//
		// So it is a separate `MagicBuffIIR` right at the spawn, NOT a field of the spawn body - 0 of those 17,717
		// bodies carries a buff list. `MagicBuffDatas.ApplyNowNoCaster` is already that form (#636's Racer buff uses
		// it), so nothing new goes on the wire.
		//
		// ⚠️ It carries the REMAINING time, not the base duration, and NOT via the stamp: `ApplyNowNoCaster` computes
		// the stamp from the remaining time it is given, so both agree. Sending the full duration and letting the
		// stamp shorten it is the shape #601 was closed for proposing - it appears nowhere in the corpus.
		//
		// ⚠️ What one sighting does NOT settle is WHICH buffs qualify. 1 of 2,929 says it is not every buff on every
		// stream-in, and the one we have is a 32-day title buff. We send every TIMED buff the entity holds, which is
		// the narrowest thing we can do that is still useful - our own `ActiveBuffs` only ever holds self-buffs, so
		// this is another player's own buffs and nothing else. Open work tracked in Gitea #677.
		private async Task SendActiveBuffsOnStreamIn(INetworkClient recipient, WorldEntity subject)
		{
			const uint UnspecifiedCaster = 0;

			if (subject.ActiveBuffs.IsEmpty)
				return;

			var now = DateTimeOffset.UtcNow;
			foreach (var (wireId, buff) in subject.ActiveBuffs.ToArray())
			{
				// An untimed buff has no remaining time to state, and a nearly-expired one would arrive as a 0 the
				// client renders as a stuck icon. Both are skipped rather than rounded.
				if (buff.ExpiresAt is not { } expiresAt)
					continue;

				var remainingCs = (uint)Math.Max(0, (expiresAt - now).TotalSeconds * 100);
				if (remainingCs == 0)
					continue;

				var record = MagicBuffCodec.Build(subject.Handle, UnspecifiedCaster, wireId,
					MagicBuffDatas.ApplyNowNoCaster(MagicBuffIds.BaseOf(wireId), remainingCs, subject.Level));

				await recipient.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, recipient.Endpoint(RpcInterfaces.GameInterfaceType),
					GameInterfaceMethods.DispatchPassBlob, s => s.WriteArrayUInt32Length(record)));
			}
		}

		// Resolve the PLAYER's instance, not the default one: the beach is Solo-instanced (#268), so the player and
		// their own escortee live in a private playfield copy (e.g. 0x00A00001), not the shared instance 0. Find the
		// map's live zone that actually contains this player, then look up the escortee (by `match`) in THAT copy - a
		// TryGetZone(mapId) default-instance lookup misses her entirely and the follow silently no-ops.
		private bool SetNpcFollow(uint mapId, uint playerHandle, bool follow, Func<WorldEntity, bool> match, string label)
		{
			var zone = _sim.World.ZonesForMap(mapId).FirstOrDefault(z => z.TryGet(playerHandle, out _));

			var npc = zone?.Entities.FirstOrDefault(e => !e.IsPlayer && match(e));
			if (npc == null)
				return false;

			npc.FollowTargetHandle = follow
				? playerHandle
				: null;
			_logger.LogInformation("Follow: {Label} {State} player 0x{Player:X8}.", label, follow
				? "now follows"
				: "stopped following", playerHandle);
			return true;
		}

		// Builds one member of an ambient pool at `pos` and adds it to the zone (no wire send - the interest stream, or
		// the maintenance caller, does that). The member roams the pool's points and fades-not-revives on death so the
		// POOL owns its respawn. BuildFromType throws if the pool's type has no lifted stat core (a misconfigured pool).
		private WorldEntity SpawnAmbientMember(AmbientPool pool, Vector3 pos, Zone zone)
		{
			var handle = NextRuntimeHandle();
			// Bake THIS zone's live token (4-byte) so the body is wire-correct and SendWorldSpawn can re-stamp it per
			// recipient. Passing 0 shipped a 1-byte token that the client read as a version-byte desync (crash on 4010's
			// ambient pools) and that SpawnTransformPatch could not fix (it only overwrites a 4-byte token in place).
			var body = NpcSpawnBuilder.BuildFromType(pool.Type, handle, pos, Quaternion.Identity, zone.PlayfieldToken);

			var entity = WorldEntity.ForNpc(handle, zone.MapId, pos, Quaternion.Identity, pool.Type.Name, EntityVitals.FromMax(pool.MaxHealth, 0, pool.MaxHealth), pool.Type.TemplateId);
			entity.InstanceId = zone.InstanceId;
			entity.Level = pool.Level;
			entity.Disposition = pool.Disposition;
			entity.Hostile = pool.Disposition == WorldEntity.DispositionHostile;
			entity.LootContainer = pool.Type.LootContainerTemplateId; // drop this type's container on death
			entity.SpawnBody = body;
			entity.SpawnAreaId = pool.AreaId;
			entity.OverheadNameOverride = pool.Label; // #363 dev nameplate label, sent via SetNameIIR when .togglenpcgroup is on
			entity.NoRespawn = true; // corpse fades on death; the POOL respawns a replacement (below)
			entity.PatrolWaypoints = pool.Points; // roam the group's area via the #266 wander (no new movement code)
			entity.ArmPatrolCycle(); // stagger the first wander so they don't all step at once
			foreach (var s in pool.Type.Spells)
				entity.Spells.Add(new NpcSpellCast(s.SpellId, NpcSpellEffectColumns.ToBuffData(s.SpellId, s.Duration, s.DescriptorSlot3, s.CastingLevel, s.FlagA, s.DescriptorSlot0, s.DescriptorSlot8)));

			zone.Add(entity);
			return entity;
		}

		// #502 scripted encounter: replace a LURE with its BOSS, on the lure's exact spot. An entity SWAP, not a
		// re-template - which is what the capture shows (Damsel handle 0x0002db79 out, Eerika 0x0002ddd1 in, same
		// second). The boss carries the lure's type so its corpse can restore it (EncounterReset).
		//
		// The boss is spawned FROM ITS TYPE: she has no NpcSpawn row and must not have one, existing only inside a
		// triggered encounter (#501).
		public async Task<WorldEntity?> SwapEncounterLureForBoss(WorldEntity lure)
		{
			ArgumentNullException.ThrowIfNull(lure);
			if (lure.EncounterBossTemplateId is not { } bossTemplateId)
				return null;

			var boss = await _database.GetNpcByTemplateId(bossTemplateId);
			if (boss?.StatCore?.Health is not { } bossHealth || boss.StatCore.Level is not { } bossLevel)
			{
				// Loudly, not silently: an encounter whose boss type never got harvested would otherwise delete its
				// lure and put nothing in its place, leaving an empty encounter no player could ever trigger again.
				_logger.LogWarning("Encounter: lure {Lure} on map {Map} has no usable boss type {Boss} - leaving the lure in place.", lure.Name, lure.MapId, bossTemplateId);
				return null;
			}

			var zone = _sim.World.ZoneForEntity(lure);
			var position = lure.Position;
			var rotation = lure.Rotation;

			// Remove the lure for every viewer that has it, then drop it from the model - the same pair
			// DespawnEntityForPlayer does, but driven from the entity rather than a caller's client.
			await BroadcastEntityDespawn(lure);
			_sim.World.Remove(lure);

			var handle = NextRuntimeHandle();
			var body = NpcSpawnBuilder.BuildFromType(boss, handle, position, rotation, zone.PlayfieldToken);
			var entity = WorldEntity.ForNpc(handle, zone.MapId, position, rotation, boss.Name, EntityVitals.FromMax(bossHealth, 0, bossHealth), boss.TemplateId);
			entity.InstanceId = zone.InstanceId;
			entity.Level = bossLevel;
			entity.Disposition = WorldEntity.DispositionHostile; // the trap sprung - she attacks
			entity.Hostile = true;
			entity.LootContainer = boss.LootContainerTemplateId;
			entity.SpawnBody = body;
			entity.NoRespawn = true; // her corpse fades; the LURE comes back instead (EncounterReset)
			entity.EncounterLureTemplateId = lure.NpcTemplateId;
			foreach (var sp in boss.Spells)
				entity.Spells.Add(new NpcSpellCast(sp.SpellId, NpcSpellEffectColumns.ToBuffData(sp.SpellId, sp.Duration, sp.DescriptorSlot3, sp.CastingLevel, sp.FlagA, sp.DescriptorSlot0, sp.DescriptorSlot8)));

			// Added to the zone only: the interest manager streams a new entity to nearby players on its next pass,
			// which is how the ambient pools spawn members mid-session too. No explicit broadcast to get wrong.
			zone.Add(entity);
			_logger.LogInformation("Encounter: {Lure} on map {Map} sprang into {Boss} (level {Level}) at ({X:F1}, {Z:F1}).", lure.Name, lure.MapId, boss.Name, entity.Level, position.X, position.Z);
			return entity;
		}

		// #528 quest ambush: a goal just advanced for the first time and it springs an NPC on the player. Spawned FROM
		// ITS TYPE beside them, hostile, with no placement anywhere - the same shape as an encounter boss, triggered by
		// quest progress instead of proximity.
		//
		// The delay is real, not cosmetic: in the capture the trigger EFFECTS (item grant, goal progress, feedback) all
		// land at t=315.6 and the body arrives at t=318.6 - two distinct steps. It is scheduled rather than awaited so
		// the pickup's own reply chain is not held up behind it.
		//
		// Retail anchors the spawn on the interacted OBJECT (3.6 u from the brick); this anchors on the PLAYER standing
		// at it, which is the same spot to within the interaction range and needs no object identity to reach the
		// progression engine. The offset direction is the player's own facing, so it lands in front of them.
		public void ScheduleQuestAmbush(INetworkClient client, NpcAmbush ambush)
		{
			ArgumentNullException.ThrowIfNull(client);
			ArgumentNullException.ThrowIfNull(ambush);

			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return;

			var zone = _sim.World.ZoneForEntity(player);
			var facing = player.Rotation;
			var forward = Vector3.Transform(new Vector3(0, 0, 1), facing);
			var position = player.Position + forward * ambush.AnchorDistance;

			var delayMs = (int)MathF.Max(0, ambush.DelaySeconds * 1000);
			Task.RunAfter(delayMs, async () =>
			{
				// The player may have logged out, zoned or died in the delay - an ambush with nobody to ambush is
				// dropped rather than left standing in an empty quarry.
				if (client.Session.Account == null || _sim.PlayerWorldEntity(client) is not { } stillHere || stillHere.MapId != zone.MapId)
					return;

				await SpawnAmbusher(zone, ambush, position, facing);
			}, _logger);
		}

		private async Task SpawnAmbusher(Zone zone, NpcAmbush ambush, Vector3 position, Quaternion rotation)
		{
			var type = await _database.GetNpcByTemplateId(ambush.NpcTemplateId);
			if (type?.StatCore?.Health is not { } health || type.StatCore.Level is not { } level)
			{
				// Loudly, not silently: an ambush whose type never got harvested would spring nothing at all, and the
				// quest would look exactly the same as one with no ambush wired.
				_logger.LogWarning("Ambush: goal {Goal} on map {Map} names type {Type}, which has no usable stat core - nothing sprung.", ambush.GoalId, zone.MapId, ambush.NpcTemplateId);
				return;
			}

			var handle = NextRuntimeHandle();
			var entity = WorldEntity.ForNpc(handle, zone.MapId, position, rotation, type.Name, EntityVitals.FromMax(health, 0, health), type.TemplateId);
			entity.InstanceId = zone.InstanceId;
			entity.Level = level;
			entity.Disposition = WorldEntity.DispositionHostile; // it is an ambush - it attacks on arrival
			entity.Hostile = true;
			entity.LootContainer = type.LootContainerTemplateId;
			entity.SpawnBody = NpcSpawnBuilder.BuildFromType(type, handle, position, rotation, zone.PlayfieldToken);
			entity.NoRespawn = true; // it corpse-fades; the ambush is a one-off, not a placement that comes back
			foreach (var sp in type.Spells)
				entity.Spells.Add(new NpcSpellCast(sp.SpellId, NpcSpellEffectColumns.ToBuffData(sp.SpellId, sp.Duration, sp.DescriptorSlot3, sp.CastingLevel, sp.FlagA, sp.DescriptorSlot0, sp.DescriptorSlot8)));

			// Zone-only, as the encounter swap and the ambient pools are: the interest pass streams it to everyone in
			// range on its next tick, and the sim's own aggro pass puts the player on its hate list - which is what the
			// capture shows arriving with it, rather than a hate list we would have to author.
			zone.Add(entity);
			_logger.LogInformation("Ambush: goal {Goal} sprang {Name} (level {Level}) on map {Map} at ({X:F1}, {Z:F1}).", ambush.GoalId, type.Name, level, zone.MapId, position.X, position.Z);
		}

		// #502: the boss is gone - put her lure back where she stood, so the encounter can fire again. Spawned from the
		// lure's TYPE at the boss's position: the boss took the lure's exact spot, so that position IS the lure's home.
		public async Task<WorldEntity?> RestoreEncounterLure(WorldEntity boss)
		{
			ArgumentNullException.ThrowIfNull(boss);
			if (boss.EncounterLureTemplateId is not { } lureTemplateId)
				return null;

			var lure = await _database.GetNpcByTemplateId(lureTemplateId);
			if (lure?.StatCore?.Health is not { } lureHealth || lure.StatCore.Level is not { } lureLevel)
				return null;

			var zone = _sim.World.ZoneForEntity(boss);
			var encounter = await _database.GetNpcEncounter(zone.MapId, lureTemplateId);
			var handle = NextRuntimeHandle();
			var entity = WorldEntity.ForNpc(handle, zone.MapId, boss.Position, boss.Rotation, lure.Name, EntityVitals.FromMax(lureHealth, 0, lureHealth), lure.TemplateId);
			entity.InstanceId = zone.InstanceId;
			entity.Level = lureLevel;
			entity.SpawnBody = NpcSpawnBuilder.BuildFromType(lure, handle, boss.Position, boss.Rotation, zone.PlayfieldToken);
			entity.NoRespawn = true;
			ArmEncounter(entity, encounter);

			zone.Add(entity);
			_logger.LogInformation("Encounter: {Lure} restored on map {Map} at ({X:F1}, {Z:F1}).", lure.Name, zone.MapId, boss.Position.X, boss.Position.Z);
			return entity;
		}

		// Copies an encounter definition onto a live lure. Central so the spawn path and the reset path arm it
		// identically - an unarmed lure is just a harmless NPC and the encounter silently never fires.
		public static void ArmEncounter(WorldEntity lure, NpcEncounter? encounter)
		{
			ArgumentNullException.ThrowIfNull(lure);
			if (encounter == null)
				return;

			lure.EncounterBossTemplateId = encounter.BossNpcTemplateId;
			lure.EncounterBarkRadius = encounter.BarkRadius;
			lure.EncounterTriggerRadius = encounter.TriggerRadius;
			lure.EncounterBarked = false;
		}

		// Spawns a standalone IsLoot bag (its OWN handle) at a position, rendered for everyone in range - the decoupled
		// loot model: the bag is a real world object (no vitals, no respawn; tick-expired via TickExpiry), independent
		// of whatever dropped it. Used by NPC death drops (#246) so the dead NPC's handle is free to
		// respawn at its spawn point and the bag survives that respawn. Returns the bag, or null if the map has no
		// loot template.
		private async Task<WorldEntity?> SpawnLootBagAt(uint mapId, uint instanceId, Vector3 position, Quaternion rotation, IReadOnlyList<uint> itemTemplates, uint cashTin, uint allowedLooterHandle,
			string? lootSourceName = null, LootContainerTemplateId variant = LootContainerTemplateId.Bag, uint lootSourceTemplateId = 0)
		{
			// #363 container variant: default is the generic bag; a boss/raid drop passes a bigger chest (LargeChest, …).
			var template = LootContainerTemplate.Create(variant);
			if (template == null)
				return null;

			var handle = NextRuntimeHandle();

			// Resolve the rolled templates to the items the loot window can actually DISPLAY (present in the item
			// table - a harvested hash stopped being a requirement in #723) ONCE, here - so the bag's LootItems and the spliced display list share ONE ordering. A
			// non-buildable template dropped only at splice-time (the old behaviour) shifted every later display slot,
			// so a single-drag (TakeBagItem indexes LootItems by the client's display Pos) removed/granted the WRONG
			// item and the window showed one fewer than it held (#226 / handoff Problems 2+3).
			var loot = await _database.GetItems(itemTemplates);

			// Identity (unit) rotation in the wire transform: a bag has no facing, and a non-unit quaternion scales the
			// model. Built FROM SCRATCH (#249): header/transform + identity-from-columns + KdTreeTail, stamped with the
			// live playfield token directly - no captured Data, no SpawnTransformPatch pass.
			var body = WorldObjectCodec.Build(template, handle, position, Quaternion.Identity, EntityClass.LootBag, _sim.World.ZoneFor(mapId, instanceId).PlayfieldToken);

			body = WorldBroadcastBodies.WithLootContents(body, handle, cashTin, [.. loot.Select((l, i) => ((uint)i, l))]); // OUR items/cash, Pos 0..n-1

			var bag = WorldEntity.ForLoot(handle, mapId, position, rotation, template.Name ?? "Loot", body, LootBagExpirySeconds);
			bag.InstanceId = instanceId; // the bag lives in the same map COPY as whatever dropped it (#268)
			bag.CashTin = cashTin;
			bag.AllowedLooterHandle = allowedLooterHandle;
			bag.LootSourceName = lootSourceName!; // the corpse's NPC name, for LootFromNpc quest-goal matching (#261)
			bag.LootSourceTemplateId = lootSourceTemplateId; // ...and its stable type, which the goal matches first (#521)
			bag.LootItems.AddRange(loot.Select(l => l.Id)); // exactly the displayed set, same order as the window
			_sim.World.ZoneFor(mapId, instanceId).Add(bag);

			// Log the contents by display Pos so an in-client single-drag test can confirm the client's source-Pos maps
			// to the item the player actually clicked (TryLootItem 0x2046 is not in any capture - the Pos mapping is
			// verified live, not from a golden).
			if (loot.Length > 0)
				_logger.LogInformation("Loot: bag 0x{Handle:X8} contents by Pos: {Items}", handle, string.Join(", ", loot.Select((l, i) => $"#{i}=0x{l.Id:X}({l.Name})")));

			foreach (var viewer in _sim.World.ZoneFor(mapId, instanceId).PlayersInRange(position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client == null)
					continue;

				await WorldIir.Send(viewer.Client, body);
				// #263: the LootBagFullUpdateIIR alone only renders a NEW world object on a playfield (re)load - the
				// client otherwise treats it as an update to an object it doesn't have yet (verified: our bags only
				// appeared after a relog). Retail follows the FullUpdate with a NewAllowedLooterIIR at the bag's
				// mid-session spawn (capture fr1358), which is what makes the client INSTANTIATE + render it live. This
				// packet is a loot-PERMISSION grant, so it names the bag's OWNER (the killer / spawning player), not the
				// recipient - a drop belongs to whoever earned it. Fall back to the viewer when there's no owner
				// so the bag still renders.
				await WorldIir.Send(viewer.Client, NewLooterCodec.Build(NewLooters.Allowed(handle, allowedLooterHandle != 0
					? allowedLooterHandle
					: viewer.Handle)));
			}

			return bag;
		}

		// #262/#678: the visual response to harvesting a quest dynel (a quarry Stone Brick). Opens retail's sequence
		// with the fade-out and schedules the rest of it on the zone - see [[DynelVisualSequence]] for the steps and
		// their measured timings, and PlayDynelVisualStep for what each one puts on the wire.
		//
		// ⚠️ **The node DOES go away, and it comes back ~14 s later; it is not permanent and it is not destroyed.**
		// Both halves of that were misread before. The first reading was that the opening state means "vanish", so
		// the method was called `HidePickupObject`; the second (#529) was that a harvested brick is never removed at
		// all, because the same handle gets a second sequence 14 s later with no `n3ToClientQuit` between them. The
		// second sequence is the REGROWTH: no client interaction precedes it, and it is the run that SETS the brick's
		// mesh and collision rather than clearing them. What is true in both readings is that the object is never
		// destroyed - the handle survives - which is why nothing in this path may treat the node as consumed.
		//
		// The glow that marks a brick as takeable is drawn by the CLIENT off its own quest tracking, and the wire
		// says so rather than it being an assumption. The dynel's body carries `quest_item_effect`
		// (ProtoSimpleDynelIIR field 6, `QuestItemEffect`) as a bare flag that names NO quest id - and the twelve
		// bodies are byte-identical on every re-send, so nothing per-player or per-quest is encoded in them. The
		// client therefore has to supply the quest half itself, which it can: `QuestFullUpdate` gives it the goal's
		// target and required count, and the `QuestGoal` Progress/Satisfied stream keeps its progress current. Retail
		// sends no per-dynel packet when the glow goes out on the third brick - only the QuestGoal pair for the goal.
		//
		// ⚠️ So do not try to gate the glow server-side. Our job is the flag and an accurate QuestGoal stream; the
		// client owns the rest. [V0: whether the client's final gate is "goal not yet satisfied" or "held count <
		// required" - both fit, and the corpus cannot separate them because the two coincide on this quest.]
		//
		// NOT built: the particle puff retail plays over the brick (`StartParticleEffect` 1010207:3667224 at the
		// node's position, stopped a second later). The effect id is per-object CONTENT and no column holds one, so
		// building it would mean hardcoding one capture's dust cloud onto every pickup in the world.
		//
		// ⚠️ And harvesting it is NOT the way out, which #686 assumed it was. The corpus holds 192
		// `StartParticleEffect` records over 13 effect ids, but they are two different phenomena and the ATTRACTOR
		// separates them cleanly: 191 carry an attachment point (34 or 35) and are STANDING decorations bolted to a
		// bone - six of their effect ids are literally the ones the objects' own geometry nodes already declare
		// (#687). Exactly ONE record is a harvest puff: attractor 0 (free-standing at a position), effect 3667224,
		// handle 0x6AF3 - this brick, in the one capture that harvests one. So a harvested `HarvestEffectId` column
		// would hold a single row, and building the puff from it is the same hardcoding under a new name.
		public async Task MarkPickupHarvested(INetworkClient client, uint handle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return;

			var zone = _sim.World.ZoneForEntity(player);
			if (!zone.TryGetWorldObject(handle, out var node))
				return;

			await BroadcastDynelVisual(zone, node, SimpleDynelVisualChanges.FadeOut(WorldObjectColumns.TypeTagForClass(node.SubOpcode), handle, DynelVisualSequence.FadeSeconds));
			zone.StartVisualSequence(node);

			_logger.LogInformation("Harvest: '{Node}' 0x{Handle:X8} fades out; it grows back in {Seconds}s.", node.Row.Name, handle,
				(2 * DynelVisualSequence.FadeSeconds) + DynelVisualSequence.RespawnDelaySeconds);
		}

		// A harvest node's new health after a swing, to everyone who can see it (#732). Retail streams this on the very
		// same StatIIR the respawn steps use for CarsGroup - stat 0x1B HealthCurrent, addressed to the node's own
		// destructible class - and the client turns health/max into the PERCENTAGE in the node's health bar. Without
		// it a depleted node keeps reading full, which is what makes the number the player actually steers by.
		// [V2: NecroL80_mage-necromancer_login_gameplay_annotated_5, dynel 0x2688: 298 -> 198 -> 98]
		public Task BroadcastNodeHealth(INetworkClient client, LiveWorldObject node, uint health)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return Task.CompletedTask;

			var typeTag = WorldObjectColumns.TypeTagForClass(node.SubOpcode);
			return BroadcastToViewers(_sim.World.ZoneForEntity(player), node, StatCodec.Build(StatIirs.ForWorldObject(typeTag, node.Handle, CharacterStatId.HealthCurrent, health)));
		}

		// #678: one due step of a harvested object's staged visual sequence, put on the wire for everyone who can see
		// it. Each step is exactly what retail sends at that moment:
		//
		//   HarvestClearMesh       ChangeMesh(0, the object's asset TYPE) + FadeIn      - draw nothing, fade back in
		//   HarvestClearCollision  StatIIR CarsGroup + SetKdTree(0)                     - and it stops blocking
		//   RespawnFadeOut         FadeOut                                              - the regrowth begins
		//   RespawnMesh            ChangeMesh(the row's asset) + FadeIn                 - the node is drawn again
		//   RespawnCollision       StatIIR CarsGroup + SetKdTree(the row's Kdtree)      - and blocks again
		//
		// Every value comes from the object's own row: `GeometryAssetId`/`GeometryAssetType` are the pair its spawn
		// already carries, `Kdtree` is the collision mesh (the client's mesh manifest names the very same record -
		// 1010030:23748 `quest_quarry_stonebrick01` lists `<CollisionMesh2 file="168444"/>`, and 168444 is what
		// retail restores AND what the column holds), and `CarsGroup` is the same value the spawn's proto field 5
		// states. Nothing here is a constant lifted from a capture.
		public async Task PlayDynelVisualStep(DueDynelVisualStep due)
		{
			var (zone, node, step) = due;
			var row = node.Row;
			var typeTag = WorldObjectColumns.TypeTagForClass(node.SubOpcode);
			var carsGroup = StatCodec.Build(StatIirs.ForWorldObject(typeTag, node.Handle, CharacterStatId.CarsGroup, row.CarsGroup));

			switch (step)
			{
				case DynelVisualStep.HarvestClearMesh:
				{
					await BroadcastDynelVisual(zone, node, SimpleDynelVisualChanges.ChangeMesh(typeTag, node.Handle, 0, row.GeometryAssetType));
					await BroadcastDynelVisual(zone, node, SimpleDynelVisualChanges.FadeIn(typeTag, node.Handle, DynelVisualSequence.FadeSeconds));
					break;
				}

				case DynelVisualStep.HarvestClearCollision:
				{
					await BroadcastToViewers(zone, node, carsGroup);
					await BroadcastDynelVisual(zone, node, SimpleDynelVisualChanges.SetKdTree(typeTag, node.Handle, 0));
					break;
				}

				case DynelVisualStep.RespawnFadeOut:
				{
					await BroadcastDynelVisual(zone, node, SimpleDynelVisualChanges.FadeOut(typeTag, node.Handle, DynelVisualSequence.FadeSeconds));
					break;
				}

				case DynelVisualStep.RespawnMesh:
				{
					await BroadcastDynelVisual(zone, node, SimpleDynelVisualChanges.ChangeMesh(typeTag, node.Handle, row.GeometryAssetId, row.GeometryAssetType));
					await BroadcastDynelVisual(zone, node, SimpleDynelVisualChanges.FadeIn(typeTag, node.Handle, DynelVisualSequence.FadeSeconds));
					break;
				}

				case DynelVisualStep.RespawnCollision:
				{
					await BroadcastToViewers(zone, node, carsGroup);
					await BroadcastDynelVisual(zone, node, SimpleDynelVisualChanges.SetKdTree(typeTag, node.Handle, row.Kdtree));
					_logger.LogInformation("Harvest: '{Node}' 0x{Handle:X8} has grown back.", row.Name, node.Handle);
					break;
				}
			}
		}

		private Task BroadcastDynelVisual(Zone zone, LiveWorldObject node, SimpleDynelVisualChangeIIR change)
		{
			return BroadcastToViewers(zone, node, SimpleDynelVisualChangeCodec.Build(change));
		}

		// Every player near enough to see the object gets it - a world object's state is the world's, not the
		// harvester's. Gated on WorldEntered like the despawn path: a still-loading client has no dynel to change.
		private static async Task BroadcastToViewers(Zone zone, LiveWorldObject node, byte[] body)
		{
			foreach (var viewer in zone.PlayersInRange(node.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client?.Session.WorldEntered == true)
					await WorldIir.Send(viewer.Client, body);
			}
		}

		// Sends the QuestJournal list (one title line per quest). The shared area header (region/map names) is taken
		// from the first definition. The trailing "tracked quest" pointer is the captured no-track block; pinning the
		// multi-quest tracked variant needs more decode.
		// ⚠️ CURRENTLY UNCALLED, and deliberately kept. Retail bundles a QuestJournal with QuestIIR x2 +
		// QuestFullUpdate in a SINGLE frame when a quest is accepted in-session; our accept path does not emit it yet.
		// Its former caller (RestorePlayerQuests) was a fabricated login burst and is gone (#389/#390).
		//
		// ⚠️ Before reusing it: it takes the area header from `definitions[0]` and files EVERY entry under that one
		// area. That is only correct for a single-area call - it is what rendered ~28 quests from 20 areas as one
		// collapsed group in the Completed tab.
		public static Task SendQuestJournal(INetworkClient client, IReadOnlyList<QuestDefinition> definitions)
		{
			var body = QuestJournalCodec.Build(QuestJournals.ForQuests(client.Session.Character.Id, definitions));
			return client.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(body)));
		}

		// Sends the active-quest LIST (0x504B7924) that POPULATES the J-panel - WITHOUT it an accepted quest never
		// appears in the log even though the client got its QuestFullUpdate detail (decoded from the grant capture). trackedDef is the highlighted (just-accepted) quest; activeQuestIds are all the
		// player's active quests with the tracked one FIRST. The area-title slot comes from the tracked quest's def.
		public Task SendQuestLogList(INetworkClient client, QuestDefinition trackedDef, IReadOnlyList<uint> activeQuestIds)
		{
			var body = MainQuestUpdateCodec.Build(client.Session.Character.Id, trackedDef.QuestLineId, trackedDef.QuestId, activeQuestIds, QuestTextTokens.Record(trackedDef, QuestTextSlot.QuestLineTitle));
			return client.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(body)));
		}

		// The spawn area a position lands in (nearest by centroid when several overlap; bounds padded), or null.
		private static NpcSpawnArea? AreaContaining(IReadOnlyList<NpcSpawnArea> areas, Vector3 pos)
		{
			const float pad = 5f;
			NpcSpawnArea? best = null;
			var bestDistSq = float.MaxValue;
			foreach (var a in areas)
			{
				if (pos.X < a.BoundsMinX - pad || pos.X > a.BoundsMaxX + pad || pos.Z < a.BoundsMinZ - pad || pos.Z > a.BoundsMaxZ + pad)
					continue;

				var cx = (a.BoundsMinX + a.BoundsMaxX) * 0.5f;
				var cz = (a.BoundsMinZ + a.BoundsMaxZ) * 0.5f;
				var distSq = (pos.X - cx) * (pos.X - cx) + (pos.Z - cz) * (pos.Z - cz);
				if (distSq < bestDistSq)
				{
					bestDistSq = distSq;
					best = a;
				}
			}

			return best;
		}

		// #363 dev label for a captured spawn's nameplate: LINK-first - the area it's a confirmed MEMBER of
		// (NpcSpawnAreaId), even if it sits outside the bounds (a hand-linked stray); else the area its POSITION lands in
		// (so an unlinked NPC still previews which area it's in). Null when neither. See FormatAreaLabel.
		private static string? AreaLabelFor(IReadOnlyList<NpcSpawnArea> areas, NpcSpawn npc)
		{
			NpcSpawnArea? area = null;
			if (npc.NpcSpawnAreaId is { } id)
				area = areas.FirstOrDefault(a => a.Id == id);

			area ??= AreaContaining(areas, npc.Position);
			return area == null
				? null
				: FormatAreaLabel(area);
		}

		// Sends the subject's FA015807 spawn to `recipient`, re-framed to the recipient and patched to the subject's
		// CURRENT position (its captured spawn body holds its entry position, which may be stale).
		// Builds one entity's FA015807 spawn record, framed for `recipient`: the stored SpawnBody with the LIVE position
		// patched in and the playfield-instance token re-stamped to `playfieldToken`. Shared by the interest send
		// (SendWorldSpawn) and the world-entry burst (#398, BuildEntryNpcBurst) so both produce byte-identical spawn
		// records - the burst is retail's shape (close NPCs on map enter) with our DB-constructed bodies.
		// One player's FA015807, built from their CURRENT state - the same two calls the world-entry path makes, so there
		// is one construction path and no cached blob to go stale. Returns null only when the player has no live session
		// left to describe (they disconnected mid-pass); anything else that would stop the build throws.
		//
		// The transform: POSITION comes from the live entity (the authoritative one the simulation moves), rotation from
		// the character row with the same degenerate-quaternion guard world entry uses - the entity carries Euler angles,
		// and the record wants the quaternion.
		private async Task<byte[]?> BuildPlayerSpawnBody(WorldEntity subject, uint playfieldToken)
		{
			if (subject.Client?.Session.Character is not { } character)
				return null;

			// The character's worn set. An EMPTY one is a naked character, not corruption: since #750 a worn item is an
			// item instance in container 101, so there is no equipment ROW whose absence could mean anything else. (This
			// used to throw on a missing row - which was right while the row was seeded at creation and its absence
			// really was corruption.)
			var worn = await _database.GetEquippedItems(character.Id);

			var rotation = character.Rotation.LengthSquared() > 0.0001f
				? character.Rotation
				: Quaternion.Identity;

			// The scaffold half of the record is built from the character's own rows (skills, feats, appearance model),
			// which the session character carries because every loader that produces one applies the same include set
			// (WithSessionNavigations). It did not, once: the session copy came back with Body + MaxStats only, so the
			// build failed - and that failure landed on the ENTERING player, hanging them on the loading screen over a
			// row missing from somebody ELSE's session object.
			// The vitals in the record are the subject's LIVE ceiling - the same value they see on their own sheet, so
			// a third party's health bar reads what its owner's does rather than a gear-less class curve (#580).
			return PlayerSpawnBuilder.BuildFromScratch(character.Id, playfieldToken, subject.Position, rotation,
				PlayerCharacterIirs.ForCharacter(character, worn, subject.Vitals?.ToMaxStats()!), character,
				await _database.GetMorphTargetRegistry());
		}

		private static Rpc BuildWorldSpawnRecord(INetworkClient recipient, WorldEntity subject, uint playfieldToken)
		{
			var body = (byte[])subject.SpawnBody.Clone();
			SpawnTransformPatch.WritePosition(body, subject.Position);

			// Re-stamp the playfield-instance token to the LIVE value at send time. The token baked into SpawnBody at
			// map-load (NpcSpawnBuilder.TryBuildLive) is the zone's DEFAULT token - but the client only registers, and
			// then polls (0x2020), a spawn whose playfield matches the one IT loaded at world entry, and that live token
			// isn't known until the player actually enters (docs/subsystems/mid-session-spawn-blocker.md part 3). Baking the
			// load-time token shipped every NPC with a stale playfield, so the client silently dropped them all and
			// never polled -> world entry never completed. Rewrite here, once the entering player's live token exists.
			SpawnTransformPatch.TryRewritePlayfield(body, playfieldToken);

			// The u32 carrier length belongs to the SENDER (#530 rule 2) - SpawnBody is the bare record.
			return new Rpc(GameServerListener.WorldSenderEndpoint, recipient.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(body));
		}

		// The nameplate label for an area: "<group> | <tier> x<count>" (e.g. "Ape2 | id-prop x8"). ASCII-only on
		// purpose (#363): the client's nameplate font doesn't render the "·"/"×" glyphs, and our own SpawnBodyReader
		// rejects a non-ASCII overhead name - both broke the dev label silently, so keep it 7-bit.
		private static string FormatAreaLabel(NpcSpawnArea a)
		{
			return $"{a.GroupName} | {ResolvedTag(a.ResolvedBy)} x{a.MaxCount}";
		}

		// A pool point with no live member sitting on it (so a refill lands on an empty spot, not on top of a survivor);
		// falls back to a random point when they're all occupied.
		private static Vector3 FreeAmbientPoint(AmbientPool pool, IReadOnlyList<WorldEntity> live)
		{
			foreach (var pt in pool.Points)
			{
				var occupied = false;
				foreach (var e in live)
				{
					var dx = e.Position.X - pt.X;
					var dz = e.Position.Z - pt.Z;
					if (dx * dx + dz * dz < 4f)
					{
						occupied = true;
						break;
					}
				}

				if (!occupied)
					return pt;
			}

			return pool.Points[Random.Shared.Next(pool.Points.Length)];
		}

		// Short nameplate-friendly tag for a spawn area's creature-resolution provenance (#363).
		private static string ResolvedTag(SpawnResolutionSource resolvedBy)
		{
			return resolvedBy switch
			{
				SpawnResolutionSource.Name          => "name",
				SpawnResolutionSource.Containment   => "contain",
				SpawnResolutionSource.IdPropagation => "id-prop",
				SpawnResolutionSource.Manual        => "manual",
				_                                   => "unresolved"
			};
		}

		// #266 patrol: issue the NPC's captured patrol route as a MoveTask so the CLIENT walks it (retail drives idle
		// patrol client-side - the server sends the route once per spawn/respawn, not a per-tick step). The anchor /
		// current position is the NPC's live spawn position (the render-gate requires the MoveTask position to match
		// the spawn it was just sent); the waypoints are the absolute captured world coords.
		private Task SendPatrol(INetworkClient recipient, WorldEntity npc)
		{
			if (!npc.Patrols)
				return Task.CompletedTask;

			// Encode emits a bare 0x2000 body (no length prefix), so it rides WorldIir.Send.
			return WorldIir.Send(recipient, npc.WalksRoute ? BuildRouteBody(npc) : npc.PatrolBody);
		}

		// #716: the beat as this NPC is about to walk it, anchored where it NOW stands.
		//
		// The captured `PatrolBody` is anchored at the entity's SPAWN, so re-issuing it at the end of a lap - or to a
		// viewer arriving mid-beat - told the client to restart the route from the beginning, which reads as the NPC
		// teleporting back to the start. Rebuilding from the live position removes the jump.
		//
		// Rebuilding was tried before (#266) and reverted because it "lost the speed and run-crawled": the trailer slot
		// paces the walk, and a rebuild that did not supply it left the client pacing off something else. It is supplied
		// here as the WALK speed (WorldState.RouteWalkSpeed) rather than the captured scalar - the scalar is not a ground
		// speed, and feeding it back made the fast-scalar beats sprint. See #714 for the measurement.
		private byte[] BuildRouteBody(WorldEntity npc)
		{
			var ahead = PatrolRoute.FromHere(npc.PatrolWaypoints, npc.RouteIndex, npc.RouteBackwards, npc.RouteLoops);
			if (ahead.Count == 0)
				return npc.PatrolBody;

			// ⚠️ The walk STARTS where the NPC is standing, so its live position is the path's first waypoint (#714).
			//
			// Without it the path began at `ahead[0]` - the next waypoint - and the client had to cover the gap from
			// wherever it had the NPC onto the route before the timed walk began, which it does at its own speed. That
			// is the catch-up dash seen in-client, and it appeared only on the FIRST body after a spawn: every later
			// re-issue fires at a turnaround, where the NPC is already standing on `ahead[0]` and the gap is zero.
			//
			// It also makes `seconds` exact rather than approximate: the duration is now the length of the path that is
			// actually sent, not of a path plus an approach leg the client was pacing separately.
			var path = new List<Vector3>(ahead.Count + 1) { npc.Position };
			path.AddRange(ahead);

			// State how long the walk takes, so the CLIENT is paced by time rather than by its own movement-speed
			// attribute - which for an NPC is its RUN speed, and is what made the beat get sprinted and then stood
			// through.
			var seconds = PatrolRoute.TotalLength(npc.Position, ahead) / npc.RouteSpeed;

			return MoveTaskCodec.Build(new MoveTaskIIR(npc.Handle, npc.Position,
				MoveTaskNodes.PatrolTimed(path, npc.RouteSpeed, npc.Position, seconds, seconds)));
		}

		// DispatchSetQuestMarker body: [u32 0xC350][u32 npcHandle][u8 state], on the GameCharInterface dispatch
		// (same framing as the static create-editor marker burst).
		private static Task SendQuestMarker(INetworkClient client, uint npcHandle, byte state)
		{
			return client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x59), client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchSetQuestMarker,
				b => b.WriteUInt32(EntityClass.Character).WriteUInt32(npcHandle).WriteByte(state)));
		}

		// #363 pushes a SetNameIIR (0xADCE0CDA) to `recipient` overriding entity `handle`'s floating nameplate with
		// `name` - the in-world form `SetNames.Nameplate` names (nameType 3 applies the name AND refreshes the
		// already-built OverheadText). Used by the spawn path and the .togglenpcgroup toggle.
		private static Task SendSetName(INetworkClient recipient, uint handle, string name)
		{
			return recipient.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, recipient.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(SetNameCodec.Build(SetNames.Nameplate(handle, name)))));
		}

		// Frames a raw 0x2000 IIR body on the world conversation to one recipient (the generic single-recipient send the
		// dedicated builders share - e.g. the NPC MoveTask chase step).
		// Forwards a player's Move to `recipient` - the mover's own carrier bytes, only the frame receiver differs, which
		// is exactly what retail relays (see BroadcastPlayerMove). The body already names the moving entity's handle, so
		// there is nothing per-recipient to rebuild; sending it verbatim also carries whatever the flag1 layout implies
		// (the jump bit and its acceleration vector included) without this end having to understand it.
		private static Task SendWorldMove(INetworkClient recipient, byte[] moveBody)
		{
			return recipient.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, recipient.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(moveBody)));
		}

		// The runtime state of one resolved NpcSpawnArea: the creature TYPE + spawn points + the population it maintains.
		// Label is the dev nameplate tag (FormatAreaLabel), applied to members when _debugAreaLabels is on.
		private sealed record AmbientPool(uint AreaId, int MaxCount, Vector3[] Points, NpcTemplate Type, uint Level, uint MaxHealth, uint Disposition, string Label);

		// The resolved destination of a zone-line teleporter: the map to hand the player off to, and where to place
		// them on it. DestMapId == 0 means the object IS a teleporter but its destination hasn't been captured yet -
		// the caller surfaces a message instead of silently dropping the player somewhere (no fallbacks).
		public readonly record struct ZoneLineDestination(uint DestMapId, Vector3? ArrivalPosition, string DestinationName);

		// The column-driven interaction of a USED world object: its InteractAction (what the object does) plus, for a
		// teleporter, the resolved Teleport destination. Action is None when the handle isn't a DB world object at all
		// (e.g. a loot bag, a zone entity) - the caller falls through to the open-animation / loot path. Lets the
		// UseObject handler switch on the action rather than special-casing one kind.
		// WorldObjectId is the used object's STABLE row id (null when the handle resolved to nothing), so a caller can
		// look the object up in the DB - the handle itself is session-volatile and useless as a key (#523).
		public readonly record struct UsedObjectInteraction(InteractAction Action, ZoneLineDestination Teleport, uint? WorldObjectId = null);
	}
}
