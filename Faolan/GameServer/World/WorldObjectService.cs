using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Progression;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	// INTERACTING with a world object (#826): opening a door or container, sitting on a bench, harvesting a node,
	// picking up a quest item, playing a dynel's visual sequence, and the GM delete.
	//
	// Its subject is `LiveWorldObject` - a static thing streamed once at world entry and living only in the zone's
	// object table - which is a different subject from `WorldBroadcaster`'s: that class is about `WorldEntity`
	// (spawn / despawn / move / interest). That is the seam. What deliberately STAYS on the broadcaster is world-object
	// STREAMING (`BuildEntryWorldObjects` / `StreamWorldObjects`), because streaming is an interest pass and
	// `DispatchInterest` drives it - moving it here would only trade this dependency for a cycle.
	//
	// It reaches the broadcaster for exactly two ops, both pure senders: `SendWorldDespawn` (a removed object still
	// leaves the client's world the same way an entity does) and `BroadcastBehaviorToViewers` (the sit / stand-up
	// pose the onlookers must see).
	public sealed class WorldObjectService
	{
		// How far from the SEAT the player must get before the server stands them up. Retail's sit capture puts it at
		// ~1.25: the seated player is parked at Z=620.49 (pkt 6812), wobbles 0.10-0.19 while sitting, then reaches
		// Z=621.74 and the standup goes out immediately (pkt 6841 -> 6844). 2.0 was too generous to ever fire - in
		// the in-client bench test the walk-off only reached 1.78 from the seat and the server never stood the player up.
		//
		// ⚠️ A seated player still streams position syncs at (roughly) the seat position - plus a one-time seat-snap of
		// up to ~1 unit - and those must NOT be read as "left the bench" (that stood the player straight back up). Only
		// a real walk exceeds this: the seated position syncs drift < 1 unit.
		private const float SeatLeaveDistance = 1.0f;

		// The object class the sit remoteformat names (capture: startobjectaction(33)|turnon_execute(4)|TavernBenchSimpleDynel).
		private const string BenchObjectClass = "TavernBenchSimpleDynel";

		private readonly WorldBroadcaster _broadcaster;
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;
		private readonly WorldSimulation _sim;

		public WorldObjectService(WorldSimulation sim, IDatabaseRepository database, WorldBroadcaster broadcaster, ILogger logger)
		{
			_sim = sim;
			_database = database;
			_broadcaster = broadcaster;
			_logger = logger;
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
					await _broadcaster.SendWorldDespawn(viewer.Client, zone.PlayfieldToken, handle, classWord);
			}

			zone.RemoveWorldObject(handle); // stop streaming it without waiting for a zone reload
			var deleted = await _database.DeleteWorldObject(obj.Id);
			_logger.LogInformation("Delete: removed world object {Name} 0x{Handle:X8} ({Rows} row(s), by {Player}).", WorldObjectDisplay.ToWire(obj) ?? obj.Name, handle, deleted, player.Name);
			return obj;
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
					await _broadcaster.SendWorldDespawn(viewer.Client, zone.PlayfieldToken, node.Handle, classWord);
			}

			zone.RemoveWorldObject(node.Handle);
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
				? new ZoneLineDestination(destMap, obj.DestinationPosition, name, obj.DestinationPlayfieldClass ?? 0)
				: new ZoneLineDestination(0, null, name);
			return Task.FromResult(new UsedObjectInteraction(obj.InteractAction, teleport, obj.Id));
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
			await _broadcaster.BroadcastBehaviorToViewers(client, body);
			_logger.LogInformation("Sit: {Player} stood up from 0x{Handle:X8}.", player.Name, handle);
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
					await _broadcaster.BroadcastBehaviorToViewers(client, assert);
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
				await _broadcaster.BroadcastBehaviorToViewers(client, body);
			}

			client.Session.SeatedOnHandle = handle;
			client.Session.SeatedPosition = null; // anchored from the parked spot on the first move after sitting
			client.Session.SeatedHoldConfirmed = false;
			_logger.LogInformation("Sit: {Player} sat on '{Obj}' 0x{Handle:X8}.", player.Name, obj.Name, handle);
			return true;
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

		// #921: a swing landed, so the node's map pin becomes the "being gathered" icon. The pin IS a stat on the dynel
		// - `WaypointType` (0xAC), which the client's stat-apply turns straight into a minimap icon - and retail moves
		// it in two pushes per bout: `ResourceGatheringActive` beside the first swing's health drop, and the node's
		// own kind (the same value its spawn carries) 10-15 s after the last swing. Nothing goes out for a swing that
		// lands inside the window; the zone's countdown just restarts.
		//
		// ⚠️ The gathering push is sent TWICE, back to back, because that is what retail does: every one of the 672
		// captured WaypointType pushes is a doubled frame (336 pairs), while the HealthCurrent push beside it is single.
		// A node whose row holds no WaypointType never had a pin and gets nothing - there is no kind to come back to.
		// [V2: demol80-demonologist_login_gameplay_annotated_1.tshark.json dynel 0x4D0: health 351 -> 251, 0xAC = 0x1D x2, 0xAC = 0x11 x2 ten seconds later]
		public async Task BroadcastGatheringPin(INetworkClient client, LiveWorldObject node)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || node.Row.WaypointType is null)
				return;

			var zone = _sim.World.ZoneForEntity(player);
			if (!zone.MarkGathering(node))
				return;

			await BroadcastWaypointType(zone, node, WaypointType.ResourceGatheringActive);
		}

		// The gathering window ran out (the tick's call): put the node's own kind back on its pin.
		public Task RestoreNodeWaypointType(Zone zone, LiveWorldObject node)
		{
			return node.Row.WaypointType is { } kind
				? BroadcastWaypointType(zone, node, (WaypointType)kind)
				: Task.CompletedTask;
		}

		private static async Task BroadcastWaypointType(Zone zone, LiveWorldObject node, WaypointType type)
		{
			var typeTag = WorldObjectColumns.TypeTagForClass(node.SubOpcode);
			var body = StatCodec.Build(StatIirs.ForWorldObject(typeTag, node.Handle, CharacterStatId.WaypointType, (uint)type));

			for (var i = 0; i < 2; i++) // doubled, as retail sends it (see BroadcastGatheringPin)
				await BroadcastToViewers(zone, node, body);
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
	}
}
