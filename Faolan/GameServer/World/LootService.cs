using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Packets.Conan;
using Faolan.Core.Network.Protocol;
using Faolan.GameServer.Economy;
using Faolan.GameServer.Quest;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	// Loot-window interaction flow (loot-all / single-drag / cash / close), decoded in protocol_loot_interaction:
	//   UL 0x2043 TryLootAll   [loot typetag 0xC87A][bag handle]              -> grant every bag item, then fade
	//   UL 0x2044 CloseLootBag [typetag][bag handle]                          -> drop the window without taking
	//   UL 0x2045 TryLootCash  [typetag][bag handle]                          -> credit the bag's coins to the wallet
	//   UL 0x2046 TryLootItem  [typetag][bag handle][display index]           -> grant the one dragged item
	// The bag STATE + the take/fade/close wire side live on WorldBroadcaster (loot bags are world entities); this
	// service is the orchestration the dispatch switch used to carry inline - it pulls items off the bag via the
	// broadcaster and grants them through the IEconomyOutbound port (the listener implements both). Extracted out of
	// GameServerListener as a composed collaborator, mirroring ShopService (#200): the switch cases are now thin
	// one-liners delegating here, like the shop cases. Our client does NOT local-add from the bag (verified -
	// persist-only left the items un-takeable), so the server item-grant push is what actually delivers them.
	public sealed class LootService
	{
		private readonly ICharacterRepository _database;
		private readonly IEconomyOutbound _outbound;
		private readonly ILogger _logger;
		private readonly QuestProgressionService _questProgression;
		private readonly WorldBroadcaster _broadcaster;

		public LootService(WorldBroadcaster broadcaster, IEconomyOutbound outbound, ICharacterRepository database, QuestProgressionService questProgression, ILogger logger)
		{
			_broadcaster = broadcaster;
			_outbound = outbound;
			_database = database;
			_questProgression = questProgression;
			_logger = logger;
		}

		// CloseLootBag: the client closed the loot window without taking everything.
		public async Task HandleCloseLootBag(INetworkClient client, Rpc packet)
		{
			var body = packet.BodyReader();
			body.ReadUInt32(); // loot typetag (0xC87A)
			var bagHandle = body.ReadUInt32();
			await _broadcaster.CloseLootBag(client, bagHandle);
		}

		// 0x2043 TryLootAll: the loot-all click. Grant every item the bag carries into the backpack (persist + live
		// DispatchAddItem); the bag hands them over once (TakeBagItems clears them), and coins ride TryLootCash.
		public async Task HandleLootAll(INetworkClient client, Rpc packet)
		{
			var body = packet.BodyReader();
			body.ReadUInt32(); // loot typetag (0xC87A)
			var bagHandle = body.ReadUInt32();

			// Capture the bag's source NPC name BEFORE taking (loot-all empties + consumes it) for the loot-quest goal.
			var sourceNpc = _broadcaster.BagSourceName(client, bagHandle);

			var items = _broadcaster.TakeBagItems(client, bagHandle);
			foreach (var itemTemplateId in items)
				await _outbound.GrantItemToBackpack(client, itemTemplateId);
			if (items.Count > 0)
				_logger.LogInformation("Loot: granted {Count} item(s) from bag 0x{Bag:X8} to {Player}.", items.Count, bagHandle, client.Session.Character?.Name);

			// Quest chain (#261): looting an NPC's corpse bag completes a LootFromNpc goal targeting it (the scavenger's
			// key from Tuut's bag). Faithful even though the key item itself is RDB content - the bag's source IS the key.
			await _questProgression.AdvanceLootGoals(client, sourceNpc!);

			// Confirm the take + (since loot-all empties it) consume/fade the bag.
			await _broadcaster.AfterLootTake(client, bagHandle);
		}

		// 0x2045 TryLootCash: loot the bag's coins; fires on every loot alongside TryLootAll.
		public async Task HandleLootCash(INetworkClient client, Rpc packet)
		{
			var body = packet.BodyReader();
			body.ReadUInt32(); // loot typetag (0xC87A)
			var bagHandle = body.ReadUInt32();

			var character = client.Session.Character;
			var cash = _broadcaster.TakeLootCash(client, bagHandle);
			if (cash > 0 && character != null)
			{
				character.Money += cash;
				await _database.SetCharacterMoney(character.Id, character.Money);
				await _outbound.SendUpdateCash(client, character.Money, UpdateCashReason.Gain);
				_logger.LogInformation("Loot: {Player} looted {Cash} tin from bag 0x{Bag:X8} (wallet {Balance}).", character.Name, cash, bagHandle, character.Money);
			}
			else
				_logger.LogDebug("TryLootCash for bag 0x{Bag:X8}: no coins to grant.", bagHandle);
		}

		// 0x2046 TryLootItem: a single drag of one item out of the loot window.
		public async Task HandleLootItem(INetworkClient client, Rpc packet)
		{
			var body = packet.BodyReader();
			body.ReadUInt32(); // loot typetag (0xC87A)
			var bagHandle = body.ReadUInt32();
			var itemIndex = body.ReadUInt32(); // which item (display index) the player dragged

			var sourceNpc = _broadcaster.BagSourceName(client, bagHandle);
			var item = _broadcaster.TakeBagItem(client, bagHandle, (int)itemIndex);
			if (item != null)
			{
				await _outbound.GrantItemToBackpack(client, item.Value);
				await _questProgression.AdvanceLootGoals(client, sourceNpc!); // #261 loot-from-NPC goal (the key)
			}

			// Confirm the take: tell the client to remove the dragged slot (itemIndex) from the open window. AfterLootTake
			// only consumes/fades the bag once it is fully emptied, so a partial drag leaves it open with the rest.
			await _broadcaster.AfterLootTake(client, bagHandle, itemIndex);
		}
	}
}
