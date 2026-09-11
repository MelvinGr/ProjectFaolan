using System;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Progression;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     Interacting with a world object: opening a door or container, sitting on a bench, harvesting a node, picking
	///     up a quest item, playing a dynel's visual sequence, and the GM delete.
	/// </summary>
	/// <remarks>
	///     Its subject is <see cref="LiveWorldObject" />, a static thing streamed once at world entry and living only in
	///     the zone's object table, where <see cref="WorldBroadcaster" />'s subject is <see cref="WorldEntity" /> -
	///     spawn, despawn, move and interest. World-object streaming stays on the broadcaster, because streaming is an
	///     interest pass and the interest dispatch drives it; moving it here would trade this dependency for a cycle.
	///     <para>
	///         This service reaches the broadcaster for two operations, both pure senders: the world despawn, because a
	///         removed object leaves the client's world the way an entity does, and the behaviour broadcast, for the
	///         sit and stand-up poses the onlookers must see.
	///     </para>
	/// </remarks>
	public sealed class WorldObjectService
	{
		/// <summary>How far from the seat the player must get before the server stands them up.</summary>
		/// <remarks>
		///     Retail's sit capture puts it at about 1.25: the seated player is parked at Z=620.49 (packet 6812),
		///     wobbles 0.10 to 0.19 while sitting, then reaches Z=621.74 and the standup goes out immediately (packets
		///     6841 to 6844). A threshold of 2.0 never fires - an in-client walk-off only reached 1.78 from the seat.
		///     <para>
		///         A seated player still streams position syncs at roughly the seat position, plus a one-time seat snap
		///         of up to about a unit, and those must not be read as leaving the bench, which would stand the player
		///         straight back up. Only a real walk exceeds this distance: the seated syncs drift less than a unit.
		///     </para>
		/// </remarks>
		private const float SeatLeaveDistance = 1.0f;

		// The object class the sit remoteformat names; the capture reads
		// startobjectaction(33)|turnon_execute(4)|TavernBenchSimpleDynel.
		private const string BenchObjectClass = "TavernBenchSimpleDynel";

		private readonly WorldBroadcaster _broadcaster;
		private readonly IWorldRepository _database;
		private readonly ILogger _logger;
		private readonly WorldSimulation _sim;

		public WorldObjectService(WorldSimulation sim, IWorldRepository database, WorldBroadcaster broadcaster, ILogger logger)
		{
			_sim = sim;
			_database = database;
			_broadcaster = broadcaster;
			_logger = logger;
		}

		/// <summary>
		///     The GM delete of a static world object - a chest, door or dynel. These are not in the live entity model,
		///     since they stream once at world entry, so a handle that is not a live entity may still be one of them.
		/// </summary>
		/// <remarks>It despawns the object live for nearby players and deletes its row, so it is gone on relog too.</remarks>
		/// <returns>The deleted row, or null when the handle is not a known world object on the player's map.</returns>
		public async Task<WorldObject?> DeleteWorldObjectForPlayer(INetworkClient client, uint handle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return null;

			var zone = _sim.World.ZoneForEntity(player);
			if (!zone.TryGetWorldObject(handle, out var live))
				return null;

			var obj = live.Row;

			// World objects are not tracked in a viewer's Known set, so the confirmed-handle gate does not apply here:
			// despawn only for viewers that have entered the world, who received the world-object burst and can
			// therefore be told to drop the handle. A world object is removed by its own class - SimpleDynel, LootBag,
			// ZoneLabel and so on - never Character, which a client silently ignores; the class comes from its spawn.
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

		/// <summary>
		///     Whether the used world object is a quest-item pickup - a <c>QuestItemEffect</c> dynel such as a quarry
		///     Stone Brick - as opposed to a door or container.
		/// </summary>
		/// <remarks>
		///     It reports only the object kind. The pickup's effect, granting the item and advancing the collect goal,
		///     is quest-driven, so the caller does nothing when no collect quest is active, which is what gates the
		///     brick nodes to holders of the quest.
		/// </remarks>
		public Task<bool> IsQuestPickup(INetworkClient client, uint handle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return Task.FromResult(false);

			return Task.FromResult(_sim.World.ZoneForEntity(player).TryGetWorldObject(handle, out var obj) && obj.Row.QuestItemEffect is > 0);
		}

		/// <summary>
		///     A player used a static world object that is not a loot bag - a door or gate. The reply drops the door's
		///     collision and plays its open animation, matching the conans-castle capture at records 1697 and 1698.
		/// </summary>
		/// <remarks>
		///     Setting the kd-tree to 0 is what removes the collision, and <c>object_open</c> is a base animation name
		///     rather than a state string. Retail's close is the mirror image - <c>object_closed</c> plus the kd-tree
		///     restored to the object's own <c>Kdtree</c> - which is why the collision id is the object's and not a
		///     constant. The object is resolved through the zone, because the handle it was streamed with is the
		///     zone's rather than a column.
		/// </remarks>
		/// <returns>False when no such object exists, so the caller can fall through to the loot-bag path.</returns>
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

		/// <summary>
		///     The resource node the player clicked, or null when the object is not one. A query, so the caller owns
		///     the grant the way it owns the quest-item pickup's.
		/// </summary>
		public LiveWorldObject? HarvestNodeAt(INetworkClient client, uint handle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.ZoneForEntity(player).TryGetWorldObject(handle, out var live))
				return null;

			return HarvestCatalog.IsHarvestable(live.Row.DisplayCategory, live.Row.DisplayId)
				? live
				: null;
		}

		/// <summary>
		///     The achievement vista the player clicked, or null when the object is not one. A query, like
		///     <see cref="HarvestNodeAt" />, so the caller owns the send sequence and the tag award.
		/// </summary>
		public LiveWorldObject? VistaAt(INetworkClient client, uint handle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.ZoneForEntity(player).TryGetWorldObject(handle, out var live))
				return null;

			return VistaCatalog.VistaFor(live.Row.DisplayCategory, live.Row.DisplayId) != null
				? live
				: null;
		}

		/// <summary>
		///     Remove an exhausted harvest node from the live world for everyone who can see it, without touching its
		///     row.
		/// </summary>
		/// <remarks>
		///     Deliberately not <see cref="DeleteWorldObjectForPlayer" />, which deletes the row and would make a
		///     harvested node gone for good in every future session. The row stays and only this zone stops streaming
		///     it, so the node is back on the next map load - the closest approximation to retail's regrowth without a
		///     measured respawn cadence. The wire handling mirrors the delete: a world object is quit by its own class
		///     word, and the gate is whether the viewer has entered the world.
		/// </remarks>
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

		/// <summary>
		///     Resolve a used world object's interaction from its <c>InteractAction</c> and destination columns, which
		///     the world-teleports importer populates from captured use-then-teleport sequences.
		/// </summary>
		/// <remarks>
		///     A teleporter with no captured destination yields a destination map of 0, so the caller can surface a
		///     message. Same-playfield and cross-playfield teleports take the same handoff path, because the teleport
		///     changes map only when the destination map differs.
		/// </remarks>
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

		/// <summary>
		///     The fallback standup, for a player who leaves the seat without asking - a teleport, or a client that
		///     skips the request.
		/// </summary>
		/// <remarks>
		///     The normal path is <see cref="StandUp" />, called when the client's own standup request arrives;
		///     inferring it from movement is not what retail does. A no-op when the player is not seated or is still
		///     within <see cref="SeatLeaveDistance" /> of the seat; otherwise it broadcasts the standup animation to
		///     the actor and nearby viewers and clears the seated state.
		/// </remarks>
		public async Task StandUpIfMoved(INetworkClient client, Vector3 newPosition)
		{
			if (client.Session.SeatedOnHandle is null)
				return;

			// Anchor to where the client parked the character. A bench has several seats - three on the Old Tarantia
			// ones - so the sit animation snaps the player up to about 1.3 units from the position they clicked at, and
			// measuring from the click point spends the whole budget on that snap in an arbitrary direction, so a real
			// walk-off never crosses the threshold. The first move after sitting is the parked position (retail's
			// packet 6812), so it becomes the anchor.
			if (client.Session.SeatedPosition is not { } seat)
			{
				client.Session.SeatedPosition = newPosition;
				return;
			}

			if (Vector3.Distance(newPosition, seat) < SeatLeaveDistance)
				return;

			await StandUp(client);
		}

		/// <summary>
		///     Send the standup animation and clear the seated state, unconditionally. This is the path retail uses:
		///     getting up is client-initiated rather than inferred from movement - see the behaviour-1 branch in the
		///     BCC handler.
		/// </summary>
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

		/// <summary>
		///     Seat the player on a bench.
		/// </summary>
		/// <remarks>
		///     Interacting with a bench plays the actor's <c>chair_sit_calm</c> animation, a server-originated
		///     behaviour record, and marks the session seated; it renders for the actor and every nearby player who
		///     already sees them. A bench is recognised by the object name, the same stable key shop vendors and the
		///     auctioneer use.
		/// </remarks>
		/// <returns>
		///     False when the used object is not a bench, so the caller falls through to the open-object and loot paths.
		/// </returns>
		public async Task<bool> TrySitOnObject(INetworkClient client, uint handle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return false;

			if (!_sim.World.ZoneForEntity(player).TryGetWorldObject(handle, out var live) || !string.Equals(live.Row.Name, "Bench", StringComparison.OrdinalIgnoreCase))
				return false;

			var obj = live.Row;

			// The sit is a two-phase handshake (capture sit-bench, frames 6805-6813). The client sends the bench
			// UseObject twice: once before it has the pose, and again after it receives the animation. Retail answers
			// each - the first with the hold-echo (behaviour 0x12, sequence 0x0004) and the sit animation (0x2A), the
			// second with the hold-assert (0x12, sequence 0x000F) that commits the pose. Sending all three up front and
			// absorbing the duplicate leaves the client's second confirm unanswered, so the sit plays and then reverts
			// after the roughly 2.6 s animation blend. Hence: seat, echo and animate on the first request, assert once
			// on the second, and absorb any further duplicates.
			if (client.Session.SeatedOnHandle == handle)
			{
				// Phase two, the client's confirming re-send: commit the pose with the hold-assert, exactly once.
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

			// Phase one, the initial UseObject. Retail brackets the sit animation with the object-action hold state
			// (behaviour 0x12): the hold-echo first, then the animation itself. The hold-assert is not sent yet, since
			// it answers the client's phase-two re-send above.
			var sit = BehaviorControlCenterCodec.Build(BehaviorAnimations.PlayerSitOnObject(player.Handle, handle, BehaviorAnimations.SitRemoteformat(BenchObjectClass)));
			var holdEcho = BehaviorControlCenterCodec.Build(BehaviorAnimations.BenchSitState(player.Handle, BehaviorAnimations.SeqBegin, handle));

			foreach (var body in new[] { holdEcho, sit })
			{
				await WorldIir.Send(client, body);
				await _broadcaster.BroadcastBehaviorToViewers(client, body);
			}

			client.Session.SeatedOnHandle = handle;
			client.Session.SeatedPosition = null; // anchored at the parked spot on the first move after sitting
			client.Session.SeatedHoldConfirmed = false;
			_logger.LogInformation("Sit: {Player} sat on '{Obj}' 0x{Handle:X8}.", player.Name, obj.Name, handle);
			return true;
		}

		/// <summary>
		///     The visual response to harvesting a quest dynel, such as a quarry Stone Brick: it opens retail's
		///     sequence with the fade-out and schedules the rest of it on the zone.
		/// </summary>
		/// <remarks>
		///     See <see cref="DynelVisualSequence" /> for the steps and their measured timings, and
		///     <see cref="PlayDynelVisualStep" /> for what each one puts on the wire.
		///     <para>
		///         The node goes away and comes back about 14 s later. It is never destroyed: the same handle gets a
		///         second sequence with no quit record between them, and that second run is the regrowth - no client
		///         interaction precedes it, and it sets the brick's mesh and collision rather than clearing them.
		///         Nothing in this path may therefore treat the node as consumed.
		///     </para>
		///     <para>
		///         The glow that marks a brick as takeable is drawn by the client off its own quest tracking, and the
		///         wire says so: the dynel's body carries <c>quest_item_effect</c> (ProtoSimpleDynelIIR field 6) as a
		///         bare flag naming no quest id, and the twelve bodies are byte-identical on every re-send, so nothing
		///         per-player or per-quest is encoded in them. The client supplies the quest half itself, which it can,
		///         because the quest full update gives it the goal's target and required count and the goal stream
		///         keeps its progress current. Retail sends no per-dynel packet when the glow goes out on the third
		///         brick, only the goal pair. So the glow must not be gated server-side: the server's job is the flag
		///         and an accurate goal stream. [V0: whether the client's final gate is "goal not yet satisfied" or
		///         "held count below required" - both fit, and the corpus cannot separate them because the two coincide
		///         on this quest.]
		///     </para>
		///     <para>
		///         The particle puff retail plays over the brick (<c>StartParticleEffect</c> 1010207:3667224 at the
		///         node's position, stopped a second later) is deliberately not built. The effect id is per-object
		///         content and no column holds one, so building it would hardcode one capture's dust cloud onto every
		///         pickup in the world - and harvesting the ids is no way round that. The corpus holds 192 particle
		///         records over 13 effect ids, but 191 carry an attachment point (34 or 35) and are standing
		///         decorations bolted to a bone, six of whose effect ids the objects' own geometry nodes already
		///         declare. Exactly one record is a harvest puff: attractor 0, free-standing at a position, effect
		///         3667224, handle 0x6AF3 - this brick, in the one capture that harvests one.
		///     </para>
		/// </remarks>
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

		/// <summary>A harvest node's new health after a swing, sent to everyone who can see it.</summary>
		/// <remarks>
		///     Retail streams this on the same stat record the respawn steps use for <c>CarsGroup</c> - stat 0x1B,
		///     HealthCurrent, addressed to the node's own destructible class - and the client turns health over max
		///     into the percentage in the node's health bar. Without it a depleted node keeps reading full.
		///     [V2: NecroL80_mage-necromancer_login_gameplay_annotated_5, dynel 0x2688: 298 -> 198 -> 98]
		/// </remarks>
		public Task BroadcastNodeHealth(INetworkClient client, LiveWorldObject node, uint health)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return Task.CompletedTask;

			var typeTag = WorldObjectColumns.TypeTagForClass(node.SubOpcode);
			return BroadcastToViewers(_sim.World.ZoneForEntity(player), node, StatCodec.Build(StatIirs.ForWorldObject(typeTag, node.Handle, CharacterStatId.HealthCurrent, health)));
		}

		/// <summary>A swing landed, so the node's map pin becomes the "being gathered" icon.</summary>
		/// <remarks>
		///     The pin is a stat on the dynel - <c>WaypointType</c> (0xAC), which the client's stat apply turns
		///     straight into a minimap icon - and retail moves it in two pushes per bout: <c>ResourceGatheringActive</c>
		///     beside the first swing's health drop, and the node's own kind, the value its spawn carries, 10 to 15 s
		///     after the last swing. A swing that lands inside the window sends nothing and merely restarts the zone's
		///     countdown. A node whose row holds no waypoint type never had a pin and gets nothing, because there is no
		///     kind to come back to.
		///     [V2: demol80-demonologist_login_gameplay_annotated_1.tshark.json dynel 0x4D0: health 351 -> 251,
		///     0xAC = 0x1D twice, 0xAC = 0x11 twice ten seconds later]
		/// </remarks>
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

		/// <summary>
		///     The gathering window ran out, which the tick calls: put the node's own kind back on its pin.
		/// </summary>
		public static Task RestoreNodeWaypointType(Zone zone, LiveWorldObject node)
		{
			return node.Row.WaypointType is { } kind
				? BroadcastWaypointType(zone, node, (WaypointType)kind)
				: Task.CompletedTask;
		}

		private static async Task BroadcastWaypointType(Zone zone, LiveWorldObject node, WaypointType type)
		{
			var typeTag = WorldObjectColumns.TypeTagForClass(node.SubOpcode);
			var body = StatCodec.Build(StatIirs.ForWorldObject(typeTag, node.Handle, CharacterStatId.WaypointType, (uint)type));

			for (var i = 0; i < 2; i++) // doubled, as retail sends it - see BroadcastGatheringPin
				await BroadcastToViewers(zone, node, body);
		}

		/// <summary>
		///     One due step of a harvested object's staged visual sequence, put on the wire for everyone who can see it.
		/// </summary>
		/// <remarks>
		///     Each step is what retail sends at that moment:
		///     <code>
		///     HarvestClearMesh       ChangeMesh(0, the object's asset type) + FadeIn   - draw nothing, fade back in
		///     HarvestClearCollision  CarsGroup stat + SetKdTree(0)                     - and it stops blocking
		///     RespawnFadeOut         FadeOut                                           - the regrowth begins
		///     RespawnMesh            ChangeMesh(the row's asset) + FadeIn              - the node is drawn again
		///     RespawnCollision       CarsGroup stat + SetKdTree(the row's Kdtree)      - and blocks again
		///     </code>
		///     Every value comes from the object's own row: the geometry asset id and type are the pair its spawn
		///     carries, <c>Kdtree</c> is the collision mesh - the client's mesh manifest names the same record, since
		///     1010030:23748 <c>quest_quarry_stonebrick01</c> lists <c>CollisionMesh2 file="168444"</c>, which is both
		///     what retail restores and what the column holds - and <c>CarsGroup</c> is the value the spawn's protobuf
		///     field 5 states. Nothing here is a constant lifted from a capture.
		/// </remarks>
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

		private static Task BroadcastDynelVisual(Zone zone, LiveWorldObject node, SimpleDynelVisualChangeIIR change)
		{
			return BroadcastToViewers(zone, node, SimpleDynelVisualChangeCodec.Build(change));
		}

		/// <summary>
		///     Every player near enough to see the object gets it: a world object's state is the world's, not the
		///     harvester's. Gated on world entry like the despawn path, because a still-loading client has no dynel to
		///     change.
		/// </summary>
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
