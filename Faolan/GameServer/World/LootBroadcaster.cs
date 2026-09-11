using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.GameServer.Combat;
using Faolan.GameServer.Quest;
using Faolan.GameServer.Wire;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     The loot lifecycle's wire side: a killed NPC's corpse drops a bag, the bag is opened and emptied, and then
	///     the bag and the corpse fade.
	/// </summary>
	/// <remarks>
	///     It answers "what dropped and what happens when you take it", as against <see cref="WorldBroadcaster" />'s
	///     "who can see what". The bag is a <see cref="WorldEntity" /> and streams like one, but that is the visibility
	///     system serving this one, the same way it serves <see cref="NpcBroadcaster" />. There is one edge out -
	///     <see cref="WorldBroadcaster.BroadcastEntityFade" />, when the last item leaves the bag - and none in.
	///     <para>
	///         It owns <see cref="Bags" />, what is actually in each bag, so <c>LootService</c> - the inbound
	///         orchestration for the loot window - takes one collaborator rather than two.
	///     </para>
	///     <para>
	///         <see cref="RuntimeHandleAllocator" /> is shared: bags are minted here and NPCs and pets on
	///         <see cref="NpcBroadcaster" />, and a second counter would hand out colliding handles.
	///     </para>
	/// </remarks>
	public sealed class LootBroadcaster
	{
		/// <summary>
		///     How long a dropped loot bag lingers before the tick fades and removes it.
		/// </summary>
		/// <remarks>
		///     Measured from captures: every bag in the corpus that nobody emptied faded at 300 s - four of them, 299.9
		///     to 300.2, and a spread of 0.3 s over five minutes is a fixed timer. A bag looted empty is torn down
		///     within seconds of the window closing instead, which is the <see cref="AfterLootTake" /> path.
		/// </remarks>
		private const double LootBagExpirySeconds = 300.0;

		/// <summary>
		///     Retail drops the bag beside the body rather than inside it: a flat +0.8 units on the world Z axis,
		///     unrotated by the corpse's facing - 60 of 65 measured kills land on (dx, dz) = (0.0, +0.8) exactly.
		/// </summary>
		private const float LootBagCorpseOffsetZ = 0.8f;

		/// <summary>How far a loot bag may be ground-snapped from the NPC's death Y.</summary>
		/// <remarks>
		///     The snap fixes death-moment Y drift from tall or flying creatures and from MoveTask drift, which is a
		///     few metres. Beyond that the heightmap is describing the outdoor surface above an interior or cave kill
		///     rather than the floor the creature stood on, so the snap is refused and the death Y wins: outdoor kills
		///     sit about a metre off, while the cave kill that exposed this was 28 metres.
		/// </remarks>
		private const float MaxLootGroundSnap = 5f;

		private readonly WorldBroadcaster _broadcaster;
		private readonly ICatalogRepository _catalog;
		private readonly RuntimeHandleAllocator _handles;
		private readonly ILogger _logger;
		private readonly QuestMarkerResolver _questMarkers;
		private readonly WorldSimulation _sim;

		public LootBroadcaster(WorldSimulation sim, ICatalogRepository catalog, IWorldRepository world, WorldBroadcaster broadcaster, QuestMarkerResolver questMarkers,
			RuntimeHandleAllocator handles, ILogger logger)
		{
			_sim = sim;
			_catalog = catalog;
			_broadcaster = broadcaster;
			_questMarkers = questMarkers;
			_handles = handles;
			_logger = logger;
			Bags = new LootBagContents(sim, catalog, world, logger);
		}

		/// <summary>
		///     What is in each loot bag. Public because <c>LootService</c> asks it directly: taking an item out of a bag
		///     is not a broadcast.
		/// </summary>
		public LootBagContents Bags { get; }

		/// <summary>
		///     Called after items are taken from a bag, whether by loot-all or a single drag: confirm the take to the
		///     client, and tear the bag down only once it is fully emptied of both items and coins.
		/// </summary>
		/// <remarks>
		///     A partial loot leaves the bag in the world with its remaining contents, and it still fades on its own
		///     expiry. The retail loot-all finish, from the hoxxling gameplay capture at frame 4470, is LootRemoveItems,
		///     then DispatchCloseLootBag, then the current- and allowed-looter resets, then n3ToClientFade and
		///     UpdateSimpleDynel.
		/// </remarks>
		public async Task AfterLootTake(INetworkClient client, uint bagHandle, params uint[] removedPositions)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.ZoneForEntity(player).TryGet(bagHandle, out var bag))
				return;

			// The per-take acknowledgement: tell the client which display slots to remove from the open window. The
			// client removes them and compacts, and a partial take leaves the bag open with its remaining items. Do not
			// re-render it - re-sending the full object stacks a second window.
			//
			// The position list is a real list rather than a one-element formality. Measured over all 148
			// LootRemoveItems records in the corpus, the element count takes 0, 1, 2, 3 and 4, and the positions are
			// the contiguous run [0..n-1] in every record but one: loot-drag-1538's single drag of slot 1, which sends
			// exactly [1]. A loot-all of n items is therefore one message listing all n slots, not n messages.
			await WorldIir.Send(client, LootRemoveItemsCodec.Build(bag.Handle, removedPositions ?? []));

			if (bag.LootItems.Any(i => i != 0) || bag.CashTin > 0)
				return; // items or coins remain (a 0 slot is drag-looted): keep the bag open, the client closes it

			// Fully emptied, so run the captured completion sequence: DispatchCloseLootBag, the looter resets, the
			// fade, and the consumed visual. The LootRemoveItems has already gone out above.
			await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x58), client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchCloseLootBag,
				b => b.WriteUInt32(EntityClass.LootBag).WriteUInt32(bag.Handle)));
			await WorldIir.Send(client, NewLooterCodec.Build(NewLooters.Current(bag.Handle, 0)));
			await WorldIir.Send(client, NewLooterCodec.Build(NewLooters.Allowed(bag.Handle, 0)));
			// The fade and the consumed visual go to everyone in range, not only the looter: the three messages above
			// are the looter's - their window closes, their looter rights reset - but the bag leaving the world is a
			// world event. Sent only to the looter, the bag stays on every other player's screen, along with whatever
			// the client draws on a lootable.
			await _broadcaster.BroadcastEntityFade(bag);

			_sim.World.ZoneForEntity(bag).Remove(bag.Handle);
			_logger.LogInformation("Loot: bag 0x{Handle:X8} fully looted; consumed + faded.", bag.Handle);
		}

		/// <summary>A player closed the loot window.</summary>
		/// <remarks>
		///     What happens to the bag depends on what is left in it rather than on the close, because closing the
		///     window is not taking the loot: a bag that still holds items or coins stays in the world so the player
		///     can come back to it.
		///     <list type="bullet">
		///         <item>loot-all empties the bag, so <see cref="AfterLootTake" /> tears it down and this handler finds nothing;</item>
		///         <item>dragging the last item is the same path;</item>
		///         <item>dragging one of two leaves contents, so the bag is kept and expires on its own.</item>
		///     </list>
		///     <para>
		///         Nothing is consumed here, not even a bag that reads as empty. A no-drop kill leaves no bag at all -
		///         <see cref="DropCorpseLoot" /> only spawns one for a quest drop, items or cash - so the only bag that
		///         can be open and empty is the quest one: a LootFromNpc mob whose goal names no quest item drops an
		///         empty bag deliberately, because looting it is what completes the goal, which matches on the bag's
		///         source NPC rather than its contents. Consuming that on close would make the quest uncompletable.
		///     </para>
		///     <para>
		///         Whether retail keeps a part-looted bag is not something the captures can settle: all 95 closes in
		///         the corpus are of a bag that was just emptied - 89 following a loot-all, 6 following drags that took
		///         what was there - and what they show, a fade about 0.2 s after the close and an n3ToClientQuit about
		///         3.4 s after the fade, is the same empty-bag teardown <see cref="AfterLootTake" /> reproduces.
		///     </para>
		/// </remarks>
		public Task CloseLootBag(INetworkClient client, uint bagHandle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.ZoneForEntity(player).TryGet(bagHandle, out var bag) || !bag.IsLootable)
				return Task.CompletedTask;

			_logger.LogInformation("Loot: {Name} closed the window on bag 0x{Handle:X8}; {Items} item(s) + {Cash} tin left in it, kept until it expires.", player.Name, bag.Handle,
				bag.LootItems.Count(i => i != 0), bag.CashTin);
			return Task.CompletedTask;
		}

		/// <summary>
		///     An NPC just died - driven by the world tick, on the first tick after the kill: roll its drop and spawn
		///     the loot bag.
		/// </summary>
		/// <remarks>
		///     The bag is its own dynel next to the body, and the body is left alone until
		///     <see cref="BroadcastCorpseFade" /> tears it down about 30.8 s later. That is retail's timing, measured
		///     over 65 matched kills with <c>--report corpse-timing</c>: the bag is in the death's own packet burst,
		///     median delay 0.00 s, and the corpse is still standing 30 s later. See
		///     <see cref="WorldEntity.CorpseFadeSeconds" />.
		/// </remarks>
		public async Task DropCorpseLoot(WorldEntity deadNpc)
		{
			if (!deadNpc.IsDead)
				return; // revived, or never died, before the drop fired

			// A quest mob the killer needs to loot - one with an active LootFromNpc goal targeting it - always leaves a
			// bag holding the goal's quest item: it has to be lootable rather than subject to a drop roll.
			var lootGoalItem = deadNpc.LastAttackerHandle is { } looterHandle
				? await _questMarkers.ActiveLootGoalItemFor(looterHandle, deadNpc.Name, deadNpc.NpcTemplateId)
				: null;
			var questDrop = lootGoalItem.HasValue;

			List<uint> items;
			uint cash;
			if (questDrop)
			{
				// A quest loot mob's bag holds only the goal's quest item, with no random gear or cash, so the
				// objective is unambiguous: Scavenger Tuut drops just the Scavenger's Key. ActiveLootGoalItemFor only
				// returns a value when the goal names one, so there is no empty-quest-bag case to model here.
				items = [lootGoalItem!.Value];
				cash = 0;
			}
			else
			{
				var dropsContents = LootResolver.RollsDrop(Random.Shared.NextDouble());
				items = await Bags.RollLootItems(dropsContents
					? Random.Shared.Next(0, 3)
					: 0, deadNpc.MapId); // zero to two items, or none at all
				cash = dropsContents
					? LootResolver.RollCash(Random.Shared.NextDouble(), deadNpc.Level)
					: 0u;
			}

			// The bag is a separate loot entity with its own handle at the death spot, never the NPC's handle reused:
			// reusing it pins the NPC's respawn to where it died, because the client cannot move a bag handle through
			// the respawn's CharFullUpdate, and the loot vanishes when the NPC respawns. The bag lives, is looted and
			// expires on its own clock while the corpse runs its own teardown.
			//
			// A bag is spawned when there is loot, and unconditionally for a quest mob - it may be empty, since looting
			// it still completes the LootFromNpc goal, which matches on the bag's source NPC rather than its contents.
			// A kill with nothing to give leaves no bag, which is retail's behaviour too: not every corpse is lootable.
			if (questDrop || items.Count > 0 || cash > 0)
			{
				// The bag goes at the NPC's death spot horizontally - where the player expects it, rather than at the
				// killer's feet on a ranged or area kill - snapped to the terrain ground Y so a static bag mesh sits on
				// the floor instead of at the creature's death-moment Y, which varies by creature and makes bags float
				// or sink. The ground height is the RDB heightmap sample at the death X and Z.
				//
				// The snap only applies when the heightmap agrees the NPC was standing on the outdoor surface, which is
				// all the heightmap describes: an NPC killed in a cave, indoors or on an upper floor sits far below or
				// above it, and snapping there flings the bag into the open air - a creature that died at Y=153 under a
				// surface of Y=181 had its bag spawn 28 m overhead, unreachable. Outdoors the delta is about a metre,
				// which is why this only shows up on interior kills. Past the tolerance the death Y is the better
				// answer, because the creature was demonstrably standing there.
				//
				// The sample is GroundHeightNear rather than GroundHeight, and that is the difference between a bag the
				// player can see and one they cannot. The heightmap holds one height per cell, so a rock, wall or trunk
				// owns its footprint at its own height: a creature that died on the edge of a sea stack's footprint had
				// a nearest cell of 199.63 against a death Y of 145.19, the 54 m delta failed the tolerance below, and
				// its bag kept the death Y - 0.6 m under the sand, invisible and unclickable, which broke the quest
				// that needed it looted, while the beach cells a metre away read 146.1. See RdbHeightmap.HeightNear.
				//
				// The bag goes beside the body rather than under it: retail offsets it by a flat +0.8 units on Z, a
				// world-axis offset rather than one rotated by the corpse's facing - 60 of the 65 measured kills land
				// on (dx, dz) = (0.0, +0.8) exactly, whatever direction the creature was facing. The body lies there
				// for another half-minute, so a bag placed dead-centre would sit inside the mesh.
				var dropPos = deadNpc.Position with { Z = deadNpc.Position.Z + LootBagCorpseOffsetZ };
				var groundY = _sim.Terrain?.GroundHeightNear(deadNpc.MapId, dropPos.X, dropPos.Z, dropPos.Y, MaxLootGroundSnap);
				if (groundY is { } ground && MathF.Abs(ground - deadNpc.Position.Y) <= MaxLootGroundSnap)
					dropPos = dropPos with { Y = ground };
				else if (groundY is { } refused)
					// Logged rather than silent: past the tolerance the death Y is kept deliberately, for an indoor or
					// cave kill, but a bag out of reach looks identical to one that never dropped.
					_logger.LogInformation("Loot: no ground snap for {Name}'s bag - died at Y={DeathY:F2} with the terrain at Y={Ground:F2} ({Delta:F1} m apart, tolerance {Tolerance} m), so the bag keeps the death height.",
						deadNpc.Name, deadNpc.Position.Y, refused, MathF.Abs(refused - deadNpc.Position.Y), MaxLootGroundSnap);

				var bag = await SpawnLootBagAt(deadNpc.MapId, deadNpc.InstanceId, dropPos, deadNpc.Rotation, items, cash, deadNpc.LastAttackerHandle ?? 0, deadNpc.Name, deadNpc.LootContainer,
					deadNpc.NpcTemplateId);
				_logger.LogInformation(
					"Loot: {Name} 0x{Handle:X8} dropped bag 0x{Bag:X8} ({Items} items, {Cash} tin, looter 0x{Looter:X8}, quest={Quest}) on map {Map} at {Drop} (death spot {Death}).", deadNpc.Name,
					deadNpc.Handle, bag?.Handle ?? 0, items.Count, cash, deadNpc.LastAttackerHandle, questDrop, deadNpc.MapId, dropPos, deadNpc.Position);
			}
		}

		/// <summary>
		///     The corpse's own teardown, about 30.8 s after the kill: fade the body out for everyone who can see it.
		/// </summary>
		/// <remarks>
		///     The n3ToClientQuit that drops the handle follows <c>CorpseQuitDelaySeconds</c> later, and the loot bag
		///     dropped on death is untouched by both - it outlives the body and expires on its own clock.
		///     <para>
		///         Sent as a Character-class fade with a 2.0 s dissolve, which is what every captured creature fade
		///         carries, 56 of 56; the loot-bag fade's 3.0 s default on the codec is the other class's number.
		///     </para>
		/// </remarks>
		public async Task BroadcastCorpseFade(WorldEntity npc)
		{
			var fade = N3ToClientFadeCodec.Build(EntityClass.Character, npc.Handle, WorldEntity.CorpseFadeAnimationSeconds);
			foreach (var viewer in _sim.World.ZoneForEntity(npc).PlayersInRange(npc.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client == null || !viewer.Knowledge.Known.Contains(npc.Handle))
					continue;

				await viewer.Client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, viewer.Client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
					s => s.WriteArrayUInt32Length(fade)));
			}
		}

		/// <summary>
		///     A player used a world object: if it is a loot bag in their zone, open its loot window, assigning the
		///     player as the current looter so the client shows the bag's contents.
		/// </summary>
		/// <remarks>
		///     The bag stays in the world; it is consumed once emptied, not on open. See
		///     <c>protocol_loot_interaction</c> for the decoded flow.
		/// </remarks>
		/// <returns>False for a non-loot or unknown object, so the caller can surface the gap.</returns>
		public async Task<bool> OpenLootBag(INetworkClient client, uint objectHandle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.ZoneForEntity(player).TryGet(objectHandle, out var bag) || !bag.IsLootable)
				return false;

			// NewCurrentLooter rides the world conversation, GameAgent to GameInterface, as in retail: see the
			// mount/gameplay capture at frame 1477.
			await client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(NewLooterCodec.Build(NewLooters.Current(bag.Handle, player.Handle)))));

			// The window-open itself is DispatchOpenLootBag on the GameCharAgent to GameCharInterface conversation -
			// the same class pair as the spawn handshake - with a body of [loot typetag][bag handle]. It is not a
			// SimpleDynelVisualChange on the world conversation, which only plays the open animation and raises no
			// window. Decoded from the hoxxling gameplay capture at frame 1477: after this the client sends TryLootCash
			// and then TryLootAll of its own accord.
			await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x58), client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchOpenLootBag,
				b => b.WriteUInt32(EntityClass.LootBag).WriteUInt32(bag.Handle)));

			_logger.LogInformation("Loot: {Name} opened the loot window on {Bag} 0x{Handle:X8}.", player.Name, bag.Name, bag.Handle);
			return true;
		}

		/// <summary>The bag-window side of a cash take: the money row leaving the open window.</summary>
		/// <remarks>
		///     Retail answers every TryLootCash with a LootRemoveItems carrying the amount and an empty position list,
		///     the counterpart of the item take's slot list. Measured on the eight cash loots in
		///     xxx_mage-necromancer_login_play1_beach-to-enter-tortage_city, where the value equals the cash-update
		///     delta every time. Without it the window keeps showing the coins the wallet has already been credited.
		/// </remarks>
		public static Task AfterLootCash(INetworkClient client, uint bagHandle, uint cash)
		{
			return WorldIir.Send(client, LootRemoveItemsCodec.Build(new LootRemoveItemsIIR(bagHandle, [], cash)));
		}

		/// <summary>
		///     Spawns a standalone loot bag with its own handle at a position, rendered for everyone in range.
		/// </summary>
		/// <remarks>
		///     The bag is a real world object - no vitals, no respawn, expired by the tick - independent of whatever
		///     dropped it, so a dead NPC's handle is free to respawn at its spawn point and the bag survives that
		///     respawn.
		/// </remarks>
		/// <returns>The bag, or null when the map has no loot template.</returns>
		private async Task<WorldEntity?> SpawnLootBagAt(uint mapId, uint instanceId, Vector3 position, Quaternion rotation, IReadOnlyList<uint> itemTemplates, uint cashTin, uint allowedLooterHandle,
			string? lootSourceName = null, LootContainerTemplateId variant = LootContainerTemplateId.Bag, uint lootSourceTemplateId = 0)
		{
			// The container variant: the default is the generic bag, while a boss or raid drop passes a bigger chest.
			var template = LootContainerTemplate.Create(variant);
			if (template == null)
				return null;

			var handle = _handles.Next();

			// Resolve the rolled templates to the items the loot window can display - those present in the item table -
			// once, here, so the bag's LootItems and the spliced display list share one ordering. Dropping a
			// non-buildable template at splice time instead shifts every later display slot, and a single drag indexes
			// LootItems by the client's display position, so it would remove and grant the wrong item while the window
			// showed one fewer than the bag held.
			var loot = await _catalog.GetItems(itemTemplates);

			// An identity rotation goes in the wire transform: a bag has no facing, and a non-unit quaternion scales
			// the model. The body is built from scratch - header and transform, the identity from columns, and the
			// kd-tree tail - stamped with the live playfield token directly, with no captured data and no patch pass.
			var body = WorldObjectCodec.Build(template, handle, position, Quaternion.Identity, EntityClass.LootBag, _sim.World.ZoneFor(mapId, instanceId).PlayfieldToken);

			body = WorldBroadcastBodies.WithLootContents(body, handle, cashTin, [.. loot.Select((l, i) => ((uint)i, l))]); // items and cash at Pos 0..n-1

			var bag = WorldEntity.ForLoot(handle, mapId, position, rotation, template.Name ?? "Loot", body, LootBagExpirySeconds);
			bag.InstanceId = instanceId; // the bag lives in the same map copy as whatever dropped it
			bag.CashTin = cashTin;
			bag.AllowedLooterHandle = allowedLooterHandle;
			bag.LootSourceName = lootSourceName!; // the corpse's NPC name, for LootFromNpc quest-goal matching
			bag.LootSourceTemplateId = lootSourceTemplateId; // and its stable type, which the goal matches first
			bag.LootItems.AddRange(loot.Select(l => l.Id)); // exactly the displayed set, in the window's order
			_sim.World.ZoneFor(mapId, instanceId).Add(bag);

			// Log the contents by display position so an in-client single-drag test can confirm the client's source
			// position maps to the item the player clicked. TryLootItem appears in no capture, so that mapping is
			// verified live rather than against a golden.
			if (loot.Length > 0)
				_logger.LogInformation("Loot: bag 0x{Handle:X8} contents by Pos: {Items}", handle, string.Join(", ", loot.Select((l, i) => $"#{i}=0x{l.Id:X}({l.Name})")));

			foreach (var viewer in _sim.World.ZoneFor(mapId, instanceId).PlayersInRange(position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client == null)
					continue;

				await WorldIir.Send(viewer.Client, body);
				// The full update alone only renders a new world object on a playfield load: mid-session the client
				// treats it as an update to an object it does not have yet, so the bag appears only after a relog.
				// Retail follows the full update with a NewAllowedLooter at the bag's mid-session spawn (capture frame
				// 1358), and that is what makes the client instantiate and render it live. The packet is a loot
				// permission grant, so it names the bag's owner - the killer or spawning player - rather than the
				// recipient, because a drop belongs to whoever earned it; with no owner it falls back to the viewer so
				// the bag still renders.
				await WorldIir.Send(viewer.Client, NewLooterCodec.Build(NewLooters.Allowed(handle, allowedLooterHandle != 0
					? allowedLooterHandle
					: viewer.Handle)));
			}

			return bag;
		}
	}
}
