using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Constants;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.GameServer.Combat;
using Faolan.GameServer.Quest;
using Faolan.GameServer.Wire;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	// The LOOT lifecycle's wire side (#826): a killed NPC's corpse drops a bag, the bag is opened and emptied, and
	// then the bag and the corpse fade.
	//
	// Its question is "what dropped and what happens when you take it" - not `WorldBroadcaster`'s "who can see what".
	// The bag IS a WorldEntity and streams like one, but that is the visibility system SERVING this one, the same way
	// it serves NpcBroadcaster; it is not a reason for the two to be one class. One edge out
	// (`BroadcastEntityFade`, when the last item leaves the bag), none in.
	//
	// It owns `Bags` - what is actually IN each bag - so LootService, the inbound orchestration for the loot window,
	// now takes ONE collaborator instead of the broadcaster plus its Bags property.
	//
	// ⚠️ `RuntimeHandleAllocator` is SHARED (see WorldBroadcaster's field): bags are minted here, NPCs and pets on
	// NpcBroadcaster, and a second counter would hand out colliding handles.
	public sealed class LootBroadcaster
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

		// What is IN a loot bag. Public because LootService asks it directly - taking an item out of a bag is not a
		// broadcast, and routing it through a send method is what made the two look like one job.
		public LootBagContents Bags { get; }

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
			await _broadcaster.BroadcastEntityFade(bag);

			_sim.World.ZoneForEntity(bag).Remove(bag.Handle);
			_logger.LogInformation("Loot: bag 0x{Handle:X8} fully looted; consumed + faded.", bag.Handle);
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
				if (viewer.Client == null || !viewer.Knowledge.Known.Contains(npc.Handle))
					continue;

				await viewer.Client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, viewer.Client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
					s => s.WriteArrayUInt32Length(fade)));
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
			await client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
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

		// The bag-window side of a cash take (#809): retail answers every TryLootCash with a LootRemoveItems carrying
		// the amount and an EMPTY position list - the money row leaving the open window, the exact counterpart of the
		// item take's slot list. Measured on the eight cash loots in
		// xxx_mage-necromancer_login_play1_beach-to-enter-tortage_city, where the value equals the DispatchUpdateCash
		// delta every time. We were crediting the wallet and never sending this, so the window kept showing the coins.
		public static Task AfterLootCash(INetworkClient client, uint bagHandle, uint cash)
		{
			return WorldIir.Send(client, LootRemoveItemsCodec.Build(new LootRemoveItemsIIR(bagHandle, [], cash)));
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
			var loot = await _catalog.GetItems(itemTemplates);

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
	}
}
