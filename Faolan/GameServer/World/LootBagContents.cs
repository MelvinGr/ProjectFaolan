using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Network;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	// What is IN a loot bag, and taking things out of it. A loot bag is a world entity, so its contents live on that
	// entity (`WorldEntity.LootItems` / `.CashTin`); this owns the reads and writes against them, and the roll that
	// decides what a fresh bag holds.
	//
	// The third corner of the loot path, and the reason it is worth naming (#826):
	//   * `LootService`      orchestrates the client's four loot opcodes (all / single / cash / close)
	//   * `LootBagContents`  owns the bag's contents - this file
	//   * `WorldBroadcaster` spawns the bag, streams it, fades it and pushes the window updates
	// LootService used to reach through the broadcaster for the middle one, which meant "what is in the bag" and "tell
	// the client about the bag" had the same address. They are different questions and only one of them touches the
	// wire.
	//
	// ⚠️ Every method resolves the bag from the CALLER's zone (`PlayerWorldEntity` -> `ZoneForEntity` -> `TryGet`)
	// rather than trusting the handle. A handle is session-volatile and client-supplied, so a bag one player can see is
	// not automatically one another player may empty; the zone lookup is what scopes it.
	public sealed class LootBagContents
	{
		private readonly ICatalogRepository _catalog;
		private readonly IWorldRepository _world;
		private readonly ILogger _logger;
		private readonly WorldSimulation _sim;

		public LootBagContents(WorldSimulation sim, ICatalogRepository catalog, IWorldRepository world, ILogger logger)
		{
			_sim = sim;
			_catalog = catalog;
			_world = world;
			_logger = logger;
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

		public IReadOnlyList<uint> TakeBagItems(INetworkClient client, uint bagHandle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.ZoneForEntity(player).TryGet(bagHandle, out var bag) || !bag.IsLootable || bag.LootItems.All(i => i == 0))
				return [];

			// Grant the still-present items (skip already-drag-looted slots, now 0); loot-all empties + closes the bag.
			var taken = bag.LootItems.Where(i => i != 0).ToArray();
			bag.LootItems.Clear();
			_logger.LogInformation("Loot: {Player} loots {Count} item(s) from bag 0x{Handle:X8}.", player.Name, taken.Length, bagHandle);
			return taken;
		}

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

		public uint TakeLootCash(INetworkClient client, uint bagHandle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || !_sim.World.ZoneForEntity(player).TryGet(bagHandle, out var bag) || !bag.IsLootable)
				return 0;

			var cash = bag.CashTin;
			bag.CashTin = 0;
			return cash;
		}

		// A few random snapshot-buildable item templates for a loot bag. Drops from the MAP's loot pool when it has
		// one, so what a corpse holds belongs to where it died.
		public async Task<List<uint>> RollLootItems(int count, uint mapId)
		{
			if (count <= 0)
				return [];

			var items = await _world.GetMapLootSample(mapId, count);
			if (items.Length == 0)
				items = await _catalog.GetRandomBuildableItems(count); // map has no captured loot pool - fall back

			return items.Select(i => i.Id).ToList();
		}
	}
}
