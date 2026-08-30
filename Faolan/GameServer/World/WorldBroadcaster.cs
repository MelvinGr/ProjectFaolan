using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Constants;
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
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Progression;
using Faolan.GameServer.Combat;
using Faolan.GameServer.Quest;
using Faolan.GameServer.Scripting;
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
	public sealed class WorldBroadcaster
	{

		// How long a dropped loot bag lingers before the tick fades + removes it (the IsLoot expiry). Capture-measured:
		// every bag in the corpus that nobody emptied faded at 300 s (four of them, 299.9-300.2 - a spread of 0.3 s
		// over five minutes is a fixed timer, not a coincidence). Looted-empty bags are torn down within seconds of the
		// window closing instead, which is the AfterLootTake path, not this one. Was 120 s, which was invented.
		private const double LootBagExpirySeconds = 300.0;

		// Retail drops the bag BESIDE the body rather than inside it: a flat +0.8 u on the world Z axis, unrotated by
		// the corpse's facing (60 of 65 measured kills are exactly (dx, dz) = (0.0, +0.8)). See DropCorpseLoot.
		private const float LootBagCorpseOffsetZ = 0.8f;

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
		// LootBagFullUpdateIIR_c - the world-object sub-opcode a loot bag spawns as.

		// The combat-mode stat value the captured beach melee mobs send at aggro (strickenbla capture, #218): the tick
		// before each mob's first swing it pushes `CombatMode = 4`, which is what draws its weapon. (The player uses 1
		// and one non-swinging entity used 1 - the field is a stance/mode code that isn't fully decoded, but 4 is the
		// value every actual melee swinger sends.)
		private const uint NpcCombatModeValue = 4;

		// A scripted exit's two beats, from the farewell window closing - see ScheduleScriptedExit for the frames.
		private const int ScriptedExitMoveDelayMs = 4000;
		private const int ScriptedExitDespawnDelayMs = 1200;

		// Dev visualization (#363): when true, an NPC that resolves to an RDB spawn AREA has that area's label pushed as
		// its overhead nameplate (via SetNameIIR after spawn) so a manual creature-labeling pass sees which area it's in.
		// Off by default; toggled live by the `.togglenpcgroup` GM command (ToggleAreaLabels) - no config.
		//
		// INSTANCE state, not static. It is a per-world-host toggle and this class is per-host, so a static made it
		// process-global - which under `--role login --role realm=<id>` (or the all-in-one dev run) would mean one
		// GM's debug nameplates following the other realm's players around. Nothing outside this class touches it.
		private bool _debugAreaLabels;

		// Cached loot-bag template (a captured "Bag of loot" world object) per map; null values cache loot-less maps.
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;

		// _tortageIntro is constructed before the broadcaster (it needs only the sim + DB), so the interest stream can
		// call into it (Casilda's captive pose on stream-in) without a construction cycle.
		private readonly ComboService _combos;
		private readonly TortageIntroService _tortageIntro;

		private readonly WorldSimulation _sim;

		// Handles for every runtime-spawned entity (loot bags, pets, summons, ambient members). The range and why it
		// matters are documented on the allocator itself; it is composed here because nothing outside this class
		// spawns runtime entities, and a SECOND counter would hand out colliding handles.
		private readonly RuntimeHandleAllocator _handles = new RuntimeHandleAllocator();

		// #363 ambient spawn-group pools. Owns the pool state and the spawning; the streaming stays here (see
		// MaintainAmbientPools) - that split is the state-vs-wire seam of #826.
		private readonly AmbientPoolService _ambient;

		// Decides the quest-marker byte, the forced quest-item drop and a player's held-quest sets. Shared with
		// QuestBroadcaster (it caches the quest-giver and goal-target tables, so a second instance would duplicate
		// them), which is why it is injected rather than built here.
		private readonly QuestMarkerResolver _questMarkers;

		// The quest WIRE. One-way: we call in (a streamed-in NPC needs its '!' / '?'), it holds nothing of ours.
		private readonly QuestBroadcaster _quests;

		// Fills a zone with its NPCs and world objects on first entry. Loading is state; the streaming that follows is
		// ours. Forwarded to below so the listener's call sites are unchanged.
		private readonly ZonePopulationLoader _zones;

		// What is IN a loot bag. Public because LootService asks it directly rather than through us - taking an item
		// out of a bag is not a broadcast, and routing it through here is what made the two look like one job.
		public LootBagContents Bags { get; }

		// Encounter lure/boss swaps and quest ambushes. Forwarded to below: all three are PORT methods
		// (IWorldOutbound / IQuestOutbound), so the signature has to stay on us even though the work does not.
		private readonly EncounterService _encounters;

		// The ApplySpell op, as the single-op delegate seam the composition pattern prescribes (CLAUDE.md) - one
		// shared operation does not warrant its own named port. Takes the WIRE id ((base << 8) | rank). Only the
		// knockback uses it (retail applies "Knockback immunity" in the same batch as the displacement).
		//
		// BOUND rather than injected, because it is a genuine cycle: what answers it is SpellEffectOutbound, which
		// reaches BuffService, which takes THIS class. Null until bound - and null-safe, which is also what lets a
		// test construct the broadcaster without it.
		private Func<INetworkClient, uint, Task>? _applySelfBuff;

		public WorldBroadcaster(WorldSimulation sim, IDatabaseRepository database, ILogger logger, TortageIntroService tortageIntro, ComboService combos,
			QuestMarkerResolver questMarkers, QuestBroadcaster quests)
		{
			_sim = sim;
			_database = database;
			_logger = logger;
			_tortageIntro = tortageIntro;
			_combos = combos;
			_ambient = new AmbientPoolService(sim, database, _handles, combos, logger);
			_questMarkers = questMarkers;
			_quests = quests;
			_zones = new ZonePopulationLoader(sim, database, combos, _ambient, logger);
			Bags = new LootBagContents(sim, database, logger);
			_encounters = new EncounterService(sim, database, _handles, combos, BroadcastEntityDespawn, logger);
		}

		/// <summary>
		///     Binds the self-buff op (see <see cref="_applySelfBuff" />). Called once, from the GameServer ctor, as
		///     soon as the port that answers it exists.
		/// </summary>
		public void BindApplySelfBuff(Func<INetworkClient, uint, Task> applySelfBuff)
		{
			_applySelfBuff = applySelfBuff;
		}

		// #261: re-evaluate quest-gated NPC visibility for one player after their quest state changed (accept / advance /
		// complete). Reloads their active-quest set, then runs an interest pass so newly-qualified gated NPCs spawn and
		// no-longer-qualified ones despawn in-place - no player/NPC movement required.
		public async Task RefreshQuestVisibility(INetworkClient client)
		{
			var entity = _sim.PlayerWorldEntity(client);
			if (entity == null)
				return;

			await _questMarkers.LoadActiveQuestIds(entity);
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
		//
		// ⚠️ It is the ONE sender that reaches a client on nobody's schedule but the tick's, so it is also the one that
		// can reach a client that is still ENTERING. World entry places the player in the zone (which is what makes
		// them visible to this sweep) seconds before it flushes its own packets, and in that window the sweep would
		// stream every in-range NPC to a client that has not been told which playfield it is in - handing it spawns
		// stamped with the zone's minted fallback token instead of the map's. The client drops every one of them, and
		// because the sweep SEEDS them into the player's Known set, the entry burst then finds nothing in range and
		// never corrects it: every NPC in the entry radius stays invisible for the whole session while remaining
		// fully alive server-side. Hence the gate on the entry actually having been DELIVERED, not merely begun
		// (Session.WorldEntered is set well before the packets are built - see the two flags' own comments).
		public async Task ResolveInterest(INetworkClient client)
		{
			if (!client.Session.WorldEntryDelivered)
				return;

			var entity = _sim.PlayerWorldEntity(client);
			if (entity == null)
				return;

			var events = InterestManager.ResolveMove(_sim.World.ZoneForEntity(entity), entity, WorldSimulation.PlayerInterestRange, true);
			await DispatchInterest(events, null);
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
				// So does a destructible's health (#498): 142 of the 193 shipped harvest nodes were captured MID-HARVEST,
				// so replaying the captured word rendered most of a zone's nodes empty while the server held them full.
				s => s.WriteArrayUInt32Length(WorldObjectCodec.Build(obj.Row, obj.Handle, playfieldToken, obj.IsClosed, obj.Health)));
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

			// ⚠️ The hate/threat records are addressed to the TARGET, not to the NPC - it is the PLAYER's threat list
			// that gains an entry, not the creature's. Measured over the whole corpus: 270 of 270 HateListUpdates carry
			// the session player as the subject and name the other party as the target, and ThreatLevel is the player's
			// on 681 of 712. Built the other way round they are addressed to a handle the client has no threat list
			// for, so it drops them - and with them the re-poll that follows: retail's client answers this pair by
			// asking RequestAggroStatus for the NPC again (frame 8482 in the Saddur fight) and only then learns the
			// creature turned hostile. That is why a mob could attack a player while still drawing GREEN and refusing
			// to be attacked back (#839): the flip reached the server model and nothing told the client.
			var hate = HateListUpdateCodec.Build(target.Handle, npc.Handle);
			// Retail opens with threat 0 and then 1 - an initialise followed by the first real level, both on the
			// player - and climbs from there as damage accrues (3 by the second swing in that fight).
			var threatOpen = ThreatLevelCodec.Build(target.Handle, 0, 1);
			var threat = ThreatLevelCodec.Build(target.Handle, 1, 1);
			// LockedToChanged is the one that IS the NPC's: [npc] -> [target], exactly as captured (#405).
			var lockedTo = LockedToChangedCodec.Build(npc.Handle, target.Handle);

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Known.Contains(npc.Handle))
				{
					await WorldIir.Send(viewer.Client, hate);
					await WorldIir.Send(viewer.Client, threatOpen);
					await WorldIir.Send(viewer.Client, threat);
					await WorldIir.Send(viewer.Client, lockedTo);
				}
			}
		}

		// A scripted EXIT: the NPC walks to a point and then leaves the world (#839). Casilda's `RunToTortage` is the
		// case it exists for - her escort is over, so she heads for the city gate and is gone.
		//
		// The shape is retail's, measured over BOTH beach-to-city captures, and they agree to within 0.3 s. Timed from
		// the farewell window closing (`KnubotCloseChatWindow`):
		//
		//   +4.0 s   a MoveTask carries her the last few units to the gate
		//   +5.2 s   n3ToClientQuit - she is gone
		//
		// (`xxx_mage-necromancer_login_play1_beach-to-enter-tortage_city` fr11419 close, fr11438 move, fr11446 quit;
		// `xxx_mage-demo_login_play_beach-to-enter-tortage_city` fr10239, fr10254, fr10264.)
		//
		// ⚠️ Scheduled rather than awaited: the caller is finishing a conversation, and holding its reply chain for
		// five seconds would stall the window's own close.
		public void ScheduleScriptedExit(WorldEntity npc, Vector3 exit)
		{
			ArgumentNullException.ThrowIfNull(npc);
			var zone = _sim.World.ZoneForEntity(npc);

			Task.RunAfter(ScriptedExitMoveDelayMs, async () =>
			{
				// She may already be gone - the player zoned, the zone reloaded, or a second trigger fired. An exit
				// for an entity that has left is dropped rather than broadcast against a stale handle.
				if (!zone.TryGet(npc.Handle, out _))
					return;

				npc.MoveFrom = npc.Position;
				npc.Position = exit;
				await BroadcastNpcMove(npc);

				Task.RunAfter(ScriptedExitDespawnDelayMs, async () =>
				{
					if (!zone.TryGet(npc.Handle, out _))
						return;

					await BroadcastEntityDespawn(npc);
					zone.Remove(npc.Handle);
					_logger.LogInformation("Script: {Npc} reached its exit and left the world.", npc.Name);
				}, _logger);
			}, _logger);
		}

		// An NPC that was standing in the world PEACEFUL has just turned on the player: restate its collision group to
		// the mob one, for every viewer that already knows it.
		//
		// ⭐ This is what makes the flip real to the CLIENT, and without it the creature is hostile on the server only:
		// it swings, the hate list arrives, the re-poll answers cCategory 1 - and the player still sees a peaceful
		// nameplate and cannot hit back. Saddur was the reported case (#839): he attacked for 8 damage while refusing
		// to be attacked, because his `CarsGroup` was still 1, the townsfolk group he spawned in.
		//
		// Measured rather than guessed. Over all 134 captures EVERY character `CarsGroup` record carrying the mob group
		// is one of the three captured Saddur fights - three captures, three handles, no other occurrence - and in all
		// three the order is identical: `SendScryEvent "Fight"`, then this record, then the hate list, then the client's
		// own re-poll. So it is the first thing retail sends on the flip, ahead of everything we already send.
		//
		// ⚠️ Two things here are copied from the capture rather than reasoned out, and both are marked as such:
		// the record is a CHARACTER one carrying the world-object `env` byte (0x00, not the 0x01 characters normally
		// take - the same "restate the dynel layer first" shape `SkillPointService` documents), and retail sends it
		// TWICE, as two packets in one segment, in all three captures. [V0: why the second copy.]
		public async Task BroadcastNpcTurnedHostile(WorldEntity npc)
		{
			ArgumentNullException.ThrowIfNull(npc);
			var body = StatCodec.Build(StatIirs.ForCharacter(npc.Handle, (uint)CharacterStatId.CarsGroup, NpcStatCore.MobCarsGroup, StatIirs.WorldObjectEnv));

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client == null || !viewer.Known.Contains(npc.Handle))
					continue;

				await WorldIir.Send(viewer.Client, body);
				await WorldIir.Send(viewer.Client, body);
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
		// #827: the companion-follow step, in the shape retail sends it - a mode-04 LIST whose second child BINDS the
		// task to the leader (MoveTaskNodes.FollowLeader), not the bare path `BroadcastNpcMove` sends for a chase.
		//
		// The bare path is why a follower slid: it is a line the client interpolates and then sits at the end of, so
		// the server re-anchors it every ~6u and the NPC snaps between segments. The bound form hands the follow to
		// the client, which then owns the position and the walk animation together.
		public async Task BroadcastNpcFollow(WorldEntity npc, WorldEntity leader)
		{
			ArgumentNullException.ThrowIfNull(npc);
			ArgumentNullException.ThrowIfNull(leader);

			var body = MoveTaskCodec.Build(new MoveTaskIIR(npc.Handle, npc.MoveFrom, MoveTaskNodes.FollowLeader(npc.MoveFrom, npc.Position, leader.Handle)));

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Known.Contains(npc.Handle))
					await WorldIir.Send(viewer.Client, body);
			}
		}

		// Send records that describe what an ENTITY is doing to every viewer that can see it - the general form of the
		// Broadcast* methods below, for a sequence that has no single name.
		//
		// ⭐ The rule it exists to serve (#272): a record addressed to a SUBJECT, describing world-visible state, goes
		// to everyone who can see that subject. Measured, not assumed - in the two-client capture
		// (`login_2-clients_group-trade-friend`, the only one with two game connections) every subject of
		// `MoveTaskIIR` (12/12), `BehaviorControlCenterIIR` (11/11), `LookAtIIR` (8/8), `StatUpdateIIR` (9/9) and
		// `StopIIR` (4/4) appears on BOTH clients' streams. What is genuinely per-player is the player's OWN state -
		// `FullPlayerCharacterIIR` 0/2, `FeedbackIIR` 0/2, `UpdateInventoryIIR` 0/2 - and a despawn, which is per
		// viewer by interest (`N3ToClientQuitIIR` 2/44). `--report broadcast-check` re-measures it.
		//
		// ⚠️ Not every record of a broadcast TYPE is a broadcast: a `BehaviorControlCenterIIR` carrying an ACK is a
		// reply to the actor's own request, and the two-client capture contains no combat, so it cannot speak for
		// those. Reach for this when the record says what an entity DID, not when it answers what a client ASKED.
		public async Task BroadcastEntityRecords(WorldEntity subject, params byte[][] records)
		{
			ArgumentNullException.ThrowIfNull(subject);
			ArgumentNullException.ThrowIfNull(records);

			foreach (var viewer in _sim.World.ZoneForEntity(subject).PlayersInRange(subject.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client == null || !viewer.Known.Contains(subject.Handle))
					continue;

				foreach (var record in records)
					await WorldIir.Send(viewer.Client, record);
			}
		}

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
		//
		// ⭐ #803: this delivery carries `npc_stats.spawn_animation`, and it is the ONLY one that does. Retail sets
		// the flag when the entity is (re)created - measured by `--report spawn-animation`'s respawn test, which
		// matches a death to the same template coming back at the same spot and finds 20 of 35 such respawns flagged
		// against 8.1 expected from those same templates' OTHER first-sightings. The cleanest lines are the solitary
		// animals, where a positional match cannot pick up a pack-mate standing nearby: Panther 3/3 flagged against
		// 1/68, Wild Cow 2/2 against 0/147, Wild Pig 1/1 against 0/83.
		//
		// ⚠️ This UNDER-sends relative to retail, deliberately. Retail flags ~6-9% of ordinary first-sightings too -
		// creatures that respawned before the player arrived and are still inside the window - and we cannot
		// reproduce that without a duration the corpus does not contain (the death->delivery gap has an unmeasured
		// respawn timer inside it, so no flagged delivery bounds the window from below). Flagging only the respawns a
		// player actually witnesses is the subset we can justify, and it is the subset that matters visually. It also
		// sidesteps the failure #803 warned about: a rule keyed to "the entity was created recently" would flag every
		// NPC in the zone on the first login after a server start, because our world builds them all at load - a
		// respawn is a distinct event from world-load creation, so keying on it cannot do that.
		public async Task BroadcastNpcRespawn(WorldEntity npc)
		{
			if (npc.SpawnBody == null)
				return;

			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				// Re-add to Known: the corpse's quit (#246) removed the handle, so the player
				// no longer has it. Known.Add gates the spawn to viewers who actually need it + restores the #245
				// invariant (Known == "was sent"), so later move/combat broadcasts (which check Known) reach it.
				if (viewer.Client != null && viewer.Known.Add(npc.Handle))
					await SendWorldSpawn(viewer.Client, npc, spawnAnimation: true); // #266: no route replay - the tick drives a server-side wander
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
		// KNOCKBACK (#496): the server shoving an entity along a segment it did not walk. Decides WHERE the victim
		// lands, then hands the pair of records below the job of saying so.
		//
		// The wire form is decoded and byte-exact; the GAMEPLAY around it is not, and the split is worth keeping in
		// mind: the whole corpus has exactly ONE knockback, so distance, direction and how KnockbackResistance scales
		// it are a single measurement plus a reasonable shape (see Knockback). Nothing here should be quoted as
		// retail-confirmed except the packets it sends.
		//
		// ⚠️ Until #139's combo finisher wired it, nothing TRIGGERED this - a GM command was its only caller. The
		// captured knockback came from an NPC ability, but the ApplySpell in that batch is the "Knockback immunity"
		// guard rather than the knocking ability, so the corpus still does not say WHICH NPC abilities knock back;
		// guessing would put a shove on attacks that never had one.
		//
		// Returns false when the geometry is degenerate (source and victim on the same spot) or the victim resists it
		// outright.
		public async Task<bool> Knock(WorldEntity victim, Vector3 source, float distance = Knockback.DefaultDistance)
		{
			// Read the victim's own resistance rather than assuming none - the stat exists (0x040D) and this is the
			// one place that consults it. It is normally absent, in which case ResistanceScale returns 1.
			var resistance = victim.BuffedStats.GetValueOrDefault(CharacterStatId.KnockbackResistance,
				(uint)victim.GearBonuses.GetValueOrDefault(CharacterStatId.KnockbackResistance));

			var to = Knockback.Resolve(source, victim.Position, distance, resistance);
			if (to == victim.Position)
			{
				_logger.LogInformation("Knockback: {Victim} not displaced (resistance {Resistance}, source {Source} vs position {Pos}).", victim.Name, resistance, source, victim.Position);
				return false;
			}

			// Ground-snap so the shove follows the terrain instead of leaving the victim at their old altitude - the
			// difference between being pushed down a slope and being pushed into the air above it. Degrades to the
			// unsnapped point when no terrain is loaded for the map, which is the documented no-terrain behaviour
			// rather than a silent failure.
			var ground = _sim.Terrain?.GroundHeight(victim.MapId, to.X, to.Z);
			if (ground is { } y)
				to = to with { Y = y };
			else if (_sim.Terrain is { Available: true })
				_logger.LogDebug("Knockback: no ground height at ({X:F1}, {Z:F1}) on map {Map}; leaving the destination unsnapped.", to.X, to.Z, victim.MapId);

			// Retail applies "Knockback immunity" to the victim in the same batch as the displacement. Sent before the
			// move so a client that acts on it has it in hand when the shove lands.
			if (victim.Client != null && _applySelfBuff != null)
				await _applySelfBuff(victim.Client, Knockback.ImmunitySpellId << 8);

			await BroadcastKnockback(victim, to);
			return true;
		}

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

			// ⚠️ Gated on the player having a live ENTITY, which is what this check always MEANT. It used to read
			// `entity?.SpawnBody == null`, and #429 then stopped a player from ever having a SpawnBody at all (a
			// player's record is rebuilt from live state per send, because the cached one described them as they were
			// at LOGIN). That commit converted the two other places SpawnBody had "quietly doubled as the is-this-
			// spawnable-yet flag" and missed this one - so from that day every login took the early return and the
			// entry burst shipped ZERO NPCs, reporting `0 in-range NPCs` on a map holding 168 of them. Nothing here
			// needs the player's own body: the records this builds are the NPCS'.
			var entity = _sim.PlayerWorldEntity(client);
			if (entity == null)
				return (records, npcs);

			// Seed the player's active/completed quest set first so quest-gated NPCs (#261 Scavenger Tuut, #262 hide-
			// once-done) are correctly included/excluded in the burst from the very start.
			await _questMarkers.LoadActiveQuestIds(entity);

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
		// (DropCorpseLoot only spawns one for questDrop || items || cash), so the only bag that can be open and
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

		// An NPC just died (driven by the world tick, on the first tick after the kill): roll its drop and spawn the
		// loot bag. The bag is its OWN dynel next to the body, and the BODY IS LEFT ALONE - it lies there until
		// BroadcastCorpseFade tears it down ~30.8 s later.
		//
		// ⚠️ This used to be `ConvertCorpseToLoot`, and the name was the bug: it waited out a 3 s "death-animation
		// linger", spawned the bag and then despawned the corpse, so a kill left a bag where the body had been. Retail
		// does neither half of that (`--report corpse-timing`, 65 matched kills): the bag is in the death's own packet
		// burst - delay 0.00 s median - and the corpse is still standing 30 s later. See WorldEntity.CorpseFadeSeconds.
		public async Task DropCorpseLoot(WorldEntity deadNpc)
		{
			if (!deadNpc.IsDead)
				return; // revived (or never died) before the drop fired - nothing to drop

			// A quest mob the killer needs to LOOT (an active LootFromNpc goal targeting this NPC, #261) ALWAYS leaves a
			// bag holding the goal's QUEST ITEM - it must be lootable, not a 50/50 roll.
			var lootGoalItem = deadNpc.LastAttackerHandle is { } looterHandle
				? await _questMarkers.ActiveLootGoalItemFor(looterHandle, deadNpc.Name, deadNpc.NpcTemplateId)
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
				items = await Bags.RollLootItems(dropsContents
					? Random.Shared.Next(0, 3)
					: 0, deadNpc.MapId); // 0-2 items, or none
				cash = dropsContents
					? LootResolver.RollCash(Random.Shared.NextDouble(), deadNpc.Level)
					: 0u;
			}

			// DECOUPLED loot (#246): the bag is a SEPARATE IsLoot entity (its OWN handle) at the death spot, NOT the
			// NPC's handle reused. Reusing it pinned the NPC's respawn to where it died (the client can't move a bag
			// handle via the respawn's CharFullUpdate) and made the loot vanish when the NPC respawned. The bag
			// lives/loots/expires on its own (tick-managed via TickExpiry) while the corpse runs its own teardown.
			// Spawn a bag when there's loot, OR unconditionally for a quest mob (the bag may be empty - looting it still
			// completes the LootFromNpc goal, which matches on the bag's source NPC, not its contents). A kill with
			// nothing to give leaves no bag, which is retail's behaviour too - not every corpse there is lootable.
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
				//
				// ⚠️ The sample is GroundHeightNear, not GroundHeight, and that is the difference between a bag you can
				// see and one you cannot. The heightmap holds one height per cell, so a rock/wall/trunk owns its
				// footprint at its own height: Scavenger Tuut died on the edge of a sea stack's footprint, his nearest
				// cell read 199.63 against a death Y of 145.19, the 54 m delta failed the tolerance below, and his bag
				// kept the death Y - 0.6 m UNDER the sand, invisible and unclickable, which broke the quest that needed
				// it looted. The beach cells a metre away read 146.1. See RdbHeightmap.HeightNear.
				//
				// ⭐ BESIDE the body, not under it: retail offsets the bag by a flat +0.8 u on Z, and it is a
				// world-AXIS offset rather than one rotated by the corpse's facing - 60 of the 65 measured kills land on
				// (dx, dz) == (0.0, +0.8) exactly, whatever direction the creature was facing. The body is still lying
				// there for another half-minute (CorpseFadeSeconds), so a bag placed dead-centre sits inside the mesh.
				var dropPos = deadNpc.Position with { Z = deadNpc.Position.Z + LootBagCorpseOffsetZ };
				var groundY = _sim.Terrain?.GroundHeightNear(deadNpc.MapId, dropPos.X, dropPos.Z, dropPos.Y, MaxLootGroundSnap);
				if (groundY is { } ground && MathF.Abs(ground - deadNpc.Position.Y) <= MaxLootGroundSnap)
					dropPos = dropPos with { Y = ground };
				else if (groundY is { } refused)
					// Not silent: past the tolerance the death Y is kept deliberately (an indoor/cave kill), but a bag
					// that ends up out of reach looks identical to one that never dropped, so say which case this was.
					_logger.LogInformation("Loot: no ground snap for {Name}'s bag - died at Y={DeathY:F2} with the terrain at Y={Ground:F2} ({Delta:F1} m apart, tolerance {Tolerance} m), so the bag keeps the death height.",
						deadNpc.Name, deadNpc.Position.Y, refused, MathF.Abs(refused - deadNpc.Position.Y), MaxLootGroundSnap);

				var bag = await SpawnLootBagAt(deadNpc.MapId, deadNpc.InstanceId, dropPos, deadNpc.Rotation, items, cash, deadNpc.LastAttackerHandle ?? 0, deadNpc.Name, deadNpc.LootContainer,
					deadNpc.NpcTemplateId);
				_logger.LogInformation(
					"Loot: {Name} 0x{Handle:X8} dropped bag 0x{Bag:X8} ({Items} items, {Cash} tin, looter 0x{Looter:X8}, quest={Quest}) on map {Map} at {Drop} (death spot {Death}).", deadNpc.Name,
					deadNpc.Handle, bag?.Handle ?? 0, items.Count, cash, deadNpc.LastAttackerHandle, questDrop, deadNpc.MapId, dropPos, deadNpc.Position);
			}
		}

		// The corpse's own teardown, ~30.8 s after the kill: fade the BODY out for everyone who can see it. The
		// n3ToClientQuit that drops the handle follows CorpseQuitDelaySeconds later (BroadcastEntityDespawn), and the
		// loot bag it dropped on death is untouched by both - it outlives the body and expires on its own clock.
		//
		// Sent as a Character-class fade with a 2.0 s dissolve, which is what every captured creature fade carries
		// (56 of 56); the loot-bag fade's 3.0 s default on the codec is the OTHER class's number.
		public async Task BroadcastCorpseFade(WorldEntity npc)
		{
			var fade = N3ToClientFadeCodec.Build(EntityClass.Character, npc.Handle, WorldEntity.CorpseFadeAnimationSeconds);
			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client == null || !viewer.Known.Contains(npc.Handle))
					continue;

				await viewer.Client.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, viewer.Client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
					s => s.WriteArrayUInt32Length(fade)));
			}
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

		// #363 pool maintenance (called on a slow cadence from the sim tick): keep each loaded zone's ambient pools at
		// MaxCount. A killed member fades + is removed at the end of its corpse sequence (NpcCorpseRemoved), dropping
		// the pool's live count; here we respawn ONE
		// replacement per under-strength pool per pass (a gentle trickle - the cadence IS the respawn delay) at a free
		// point, streamed to nearby players.
		public async Task MaintainAmbientPools()
		{
			// The service spawns the deficit and yields each member as it goes; the streaming is ours. Because
			// Replenish is a lazy iterator, this keeps the original spawn-then-send order rather than spawning every
			// pool's deficit up front.
			foreach (var (entity, zone, pos) in _ambient.Replenish())
			{
				foreach (var viewer in zone.PlayersInRange(pos, WorldSimulation.PlayerInterestRange))
				{
					if (viewer.Client != null && viewer.Known.Add(entity.Handle))
						await SendWorldSpawn(viewer.Client, entity);
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

		// #398: the per-NPC follow-ups for the entry-burst NPCs (BuildEntryNpcBurst) - the quest markers and the
		// Casilda captive pose that DispatchInterest sends right after a streamed spawn. Sent AFTER the burst flush
		// (these are separate IIRs, not FA015807 spawn records) but still before the player starts moving.
		public async Task SendEntryNpcFollowups(INetworkClient client, List<WorldEntity> burstNpcs)
		{
			foreach (var npc in burstNpcs)
			{
				// #241: quest-giver marker from THIS viewer's quest state (! available / ? ready-to-turn-in / none).
				await _quests.SendQuestMarkerIfGiver(client, npc);

				// #267: a not-yet-freed Casilda streams in CHAINED (the captive struggle pose).
				if (npc.NpcTemplateId == TortageIntroService.CasildaTemplateId && client.Session.Character != null && !await _tortageIntro.IsCasildaFreed(client.Session.Character.Id))
					await _tortageIntro.SendCasildaPose(client, npc.Handle, true);

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

			var handle = _handles.Next(); // a fresh summoned-entity handle (high range, no player/NPC collision)
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
			entity.AttackRange = NpcWeapon.AttackRange(npc.Equipment, WorldState.NpcMeleeRange); // #838
			entity.MeleeComboId = await _combos.NpcMeleeComboId(npc.Equipment); // #623, likewise
			entity.ClassComboIds = await _combos.NpcClassComboIds(npc.Equipment, entity.Level, npc.NpcTemplate.StatCore?.ComboClass); // #675, likewise
			entity.Disposition = npc.NpcTemplate.StatCore?.Disposition ?? WorldEntity.DispositionFriendly;
			entity.Hostile = killable && entity.Disposition == WorldEntity.DispositionHostile;
			entity.LootContainer = npc.NpcTemplate.LootContainerTemplateId; // drop this type's container on death
			entity.FactionId = npc.NpcTemplate.StatCore?.FactionId; // #310
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
			var handle = _handles.Next();
			// Bake the summoner's zone token (4-byte) - see SpawnAmbientMember; a 0 token desyncs the client version read.
			var body = NpcSpawnBuilder.BuildFromType(npcType, handle, player.Position, player.Rotation, _sim.World.ZoneForEntity(player).PlayfieldToken);

			var level = npcType.StatCore?.Level ?? 0;
			var maxHealth = level > 0
				? CombatResolver.NpcMaxHealthForLevel(level)
				: 1;
			var vitals = EntityVitals.FromMax(maxHealth, 0, maxHealth);
			var entity = WorldEntity.ForNpc(handle, player.MapId, player.Position, player.Rotation, npcType.Name, vitals, npcType.TemplateId);
			entity.RangedAttacker = NpcWeapon.IsRanged(npcType.Equipment); // #671 - from the TYPE's equipment; a pet has no NpcSpawn row
			entity.AttackRange = NpcWeapon.AttackRange(npcType.Equipment, WorldState.NpcMeleeRange); // #838
			entity.MeleeComboId = await _combos.NpcMeleeComboId(npcType.Equipment); // #623, likewise
			entity.ClassComboIds = await _combos.NpcClassComboIds(npcType.Equipment, level, npcType.StatCore?.ComboClass); // #675, likewise
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

		// The bag-window side of a cash take (#809): retail answers every TryLootCash with a LootRemoveItems carrying
		// the amount and an EMPTY position list - the money row leaving the open window, the exact counterpart of the
		// item take's slot list. Measured on the eight cash loots in
		// xxx_mage-necromancer_login_play1_beach-to-enter-tortage_city, where the value equals the DispatchUpdateCash
		// delta every time. We were crediting the wallet and never sending this, so the window kept showing the coins.
		public Task AfterLootCash(INetworkClient client, uint bagHandle, uint cash)
		{
			return WorldIir.Send(client, LootRemoveItemsCodec.Build(new LootRemoveItemsIIR(bagHandle, [], cash)));
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
							await _quests.SendQuestMarkerIfGiver(recipient, ev.Subject);
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
						if (ev.Subject is { IsPlayer: false, NpcTemplateId: TortageIntroService.CasildaTemplateId } && recipient.Session.Character != null &&
						    !await _tortageIntro.IsCasildaFreed(recipient.Session.Character.Id))
							await _tortageIntro.SendCasildaPose(recipient, ev.Subject.Handle, true);
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

		// Removes the subject from `recipient`'s view - the n3ToClientQuit despawn (N3ToClientQuitCodec). Stamped with the
		// recipient's INSTANCE playfield token (#268; ZoneForEntity(recipient).PlayfieldToken at the call site): the
		// burst's value for a replayed playfield (what the client learned at entry), else WorldState's minted fallback.
		// The token-is-0 guard is just defensive (ZoneFor always mints one).
		internal Task SendWorldDespawn(INetworkClient recipient, uint playfieldToken, uint subjectHandle, uint classWord = EntityClass.Character)
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

		// `spawnAnimation` marks this ONE delivery as an entrance: the NPC (re)appeared just now, so the client should
		// play its spawn-in clip rather than fade it in standing (#803). Default false - it is true only on the
		// respawn path, which is the only creation event our world has that retail's own rule would also flag.
		private async Task SendWorldSpawn(INetworkClient recipient, WorldEntity subject, bool spawnAnimation = false)
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
				await recipient.SendAsync(BuildWorldSpawnRecord(recipient, subject, playfieldToken, spawnAnimation));
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

			var handle = _handles.Next();

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

		private static Rpc BuildWorldSpawnRecord(INetworkClient recipient, WorldEntity subject, uint playfieldToken, bool spawnAnimation = false)
		{
			// ⭐ #803: a RESPAWN delivery carries the entrance-animation trigger; every other delivery of the same
			// entity does not. Stamped before the transform patch because the re-encode rebuilds the record, which
			// would discard a patch applied to the old bytes.
			// A freed escortee is described by her FREED body - the ordinary idle instead of the story pose, and walk
			// mode (#827). `Following` is the physical form of "freed": chained to the post, or walking behind someone.
			// Without this she kept the chained struggle after her rescue, because the pose the client holds comes from
			// the spawn record and no later BCC registration replaces it.
			var source = subject is { Following: true, FreedSpawnBody: not null }
				? subject.FreedSpawnBody
				: subject.SpawnBody;
			var body = spawnAnimation
				? NpcSpawnBuilder.WithSpawnAnimation(source)
				: (byte[])source.Clone();

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

		// Zone population - loading is ZonePopulationLoader's; these forward so the listener keeps one collaborator to
		// talk to about a zone (the same forwarding the listener does for its own services, CLAUDE.md).
		public Task EnsureZoneNpcs(uint mapId, uint instanceId, ConanClass forClass)
		{
			return _zones.EnsureZoneNpcs(mapId, instanceId, forClass);
		}

		public Task EnsureZoneWorldObjects(uint mapId, uint instanceId)
		{
			return _zones.EnsureZoneWorldObjects(mapId, instanceId);
		}

		public Task<string?> DescribeMissingClassImport(uint mapId, ConanClass forClass)
		{
			return _zones.DescribeMissingClassImport(mapId, forClass);
		}

		// Encounters and ambushes - the work is EncounterService's; these keep the port signatures.
		public Task<WorldEntity?> SwapEncounterLureForBoss(WorldEntity lure)
		{
			return _encounters.SwapEncounterLureForBoss(lure);
		}

		public Task<WorldEntity?> RestoreEncounterLure(WorldEntity boss)
		{
			return _encounters.RestoreEncounterLure(boss);
		}

		public void ScheduleQuestAmbush(INetworkClient client, NpcAmbush ambush)
		{
			_encounters.ScheduleQuestAmbush(client, ambush);
		}
	}
}
