using System.Linq;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
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
		private readonly IChatServerDispatcher _chat;
		private readonly ICharacterRepository _database;
		private readonly IEconomyOutbound _outbound;
		private readonly ILogger _logger;
		private readonly QuestProgressionService _questProgression;
		private readonly WorldBroadcaster _broadcaster;

		public LootService(WorldBroadcaster broadcaster, IEconomyOutbound outbound, ICharacterRepository database, QuestProgressionService questProgression, IChatServerDispatcher chat,
			ILogger logger)
		{
			_broadcaster = broadcaster;
			_outbound = outbound;
			_database = database;
			_questProgression = questProgression;
			_chat = chat;
			_logger = logger;
		}

		// Grants one looted item, and puts it BACK on the bag if the grant fails (full backpack / an item the snapshot
		// cannot build). The bag hands each item over exactly once, so discarding the result - which is what this code
		// did - destroyed the loot and then logged the take as a success. Returns whether the player actually got it.
		private async Task<bool> GrantLootedItem(INetworkClient client, uint bagHandle, uint itemTemplateId, int? position = null, int? preferredSlot = null)
		{
			if (await _outbound.GrantItemToBackpack(client, itemTemplateId, preferredSlot: preferredSlot) != null)
				return true;

			_broadcaster.ReturnBagItem(client, bagHandle, itemTemplateId, position);
			if (client.Session.Character is { } character)
				await _chat.SendSystemMessage(character.Id, "You have no room for that - free a slot and loot it again.");

			return false;
		}

		// CloseLootBag: the client closed the loot window without taking everything.
		public async Task HandleCloseLootBag(INetworkClient client, Rpc packet, FaolanStream body)
		{
			body.ReadUInt32(); // loot typetag (0xC87A)
			var bagHandle = body.ReadUInt32();
			await _broadcaster.CloseLootBag(client, bagHandle);
		}

		// 0x2043 TryLootAll: the loot-all click. Grant every item the bag carries into the backpack (persist + live
		// DispatchAddItem); the bag hands them over once (TakeBagItems clears them), and coins ride TryLootCash.
		public async Task HandleLootAll(INetworkClient client, Rpc packet, FaolanStream body)
		{
			body.ReadUInt32(); // loot typetag (0xC87A)
			var bagHandle = body.ReadUInt32();

			// Capture the bag's source NPC BEFORE taking (loot-all empties + consumes it) for the loot-quest goal.
			var source = _broadcaster.BagSource(client, bagHandle);

			var items = _broadcaster.TakeBagItems(client, bagHandle);
			var granted = 0;
			foreach (var itemTemplateId in items)
			{
				if (!await GrantLootedItem(client, bagHandle, itemTemplateId))
					continue;

				granted++;

				// Quest chain (#261): the goal is satisfied by RECEIVING its quest item, so it
				// advances per granted item - not once per loot-all. Looting a bag without that item in it, or a
				// take that bounced off a full backpack, advances nothing.
				await _questProgression.AdvanceLootGoals(client, source.Name!, itemTemplateId, source.TemplateId);
			}

			if (items.Count > 0)
			{
				// Count what the player actually RECEIVED, not what the bag handed over - the two differ when the
				// backpack fills up mid-loot, and the returned items keep the bag open (AfterLootTake fades it only
				// once it is truly empty).
				_logger.LogInformation("Loot: granted {Granted} of {Count} item(s) from bag 0x{Bag:X8} to {Player}.", granted, items.Count, bagHandle, client.Session.Character?.Name);
			}

			// Confirm the take + (since loot-all empties it) consume/fade the bag. A loot-all removes EVERY item slot
			// the bag handed over, and retail says so in one message listing them all - the contiguous run [0..n-1]
			// (#453). Passing nothing here sent a one-element [0] and left the client showing every slot but the first.
			await _broadcaster.AfterLootTake(client, bagHandle, [.. Enumerable.Range(0, items.Count).Select(i => (uint)i)]);
		}

		// 0x2045 TryLootCash: credit the bag's coins. Coins AUTO-LOOT when the window opens - this is the client's very
		// first word about a bag, sent before the player takes anything, so it is not a "loot the money" action the
		// player can decline. Measured over the corpus: 67 of 70 bags open with it, and the canonical sequence is
		// TryLootCash -> TryLootAll -> CloseLootBag (63 bags), or TryLootCash -> TryLootItem -> CloseLootBag (4).
		// Nothing to do differently server-side - credit what the client asks for - but it is why a bag that held only
		// coins is already empty by the time any item take happens.
		public async Task HandleLootCash(INetworkClient client, Rpc packet, FaolanStream body)
		{
			body.ReadUInt32(); // loot typetag (0xC87A)
			var bagHandle = body.ReadUInt32();

			var character = client.Session.Character;
			var cash = _broadcaster.TakeLootCash(client, bagHandle);
			if (cash > 0 && character != null)
			{
				character.Money += cash;
				await _database.SetCharacterMoney(character.Id, character.Money);
				await _outbound.SendUpdateCash(client, character.Money, UpdateCashReason.Gain);
				// Retail follows the balance update with the bag-window removal of the money row (#809).
				await _broadcaster.AfterLootCash(client, bagHandle, cash);
				_logger.LogInformation("Loot: {Player} looted {Cash} tin from bag 0x{Bag:X8} (wallet {Balance}).", character.Name, cash, bagHandle, character.Money);
			}
			else
				_logger.LogDebug("TryLootCash for bag 0x{Bag:X8}: no coins to grant.", bagHandle);
		}

		// 0x2046 TryLootItem: a single drag of one item out of the loot window.
		public async Task HandleLootItem(INetworkClient client, Rpc packet, FaolanStream body)
		{
			// TryLootItem(const ID32& i_cLootBagID, int i_nItemPos, const ID32& i_cCharID, int i_nDesiredPosition) -
			// the client's own signature (docs/re/aoc-client-rpc-methods.json). The handler used to stop after
			// i_nItemPos and leave 12 bytes on the body, which hid i_nDesiredPosition entirely: the player DRAGS the
			// item onto a specific backpack slot and that word is the slot they dropped it on. The two captured
			// multi-loot sequences make the meaning unambiguous: i_nItemPos runs over the bag's display indices while
			// i_nDesiredPosition tracks the player's own filling backpack (0,1,2,...,5,6). It is now honoured (#462) -
			// as a REQUEST: an occupied or out-of-range slot still falls back to the first free one, because a drag
			// onto a full slot must put the item somewhere rather than failing or overwriting what is there.
			body.ReadUInt32(); // loot typetag (0xC87A)
			var bagHandle = body.ReadUInt32();
			var itemIndex = body.ReadUInt32(); // i_nItemPos - which item (display index) the player dragged
			body.ReadUInt32(); // EntityClass.Character (0xC350)
			var looterHandle = body.ReadUInt32(); // i_cCharID - the looter; we key off the session instead
			var desiredPosition = body.ReadUInt32();

			_logger.LogDebug("TryLootItem: bag {Bag:X8} index {Index} -> looter {Looter:X8} desired slot {Slot}.", bagHandle, itemIndex, looterHandle, desiredPosition);

			var source = _broadcaster.BagSource(client, bagHandle);
			var item = _broadcaster.TakeBagItem(client, bagHandle, (int)itemIndex);
			if (item != null && await GrantLootedItem(client, bagHandle, item.Value, (int)itemIndex, (int)desiredPosition))
				await _questProgression.AdvanceLootGoals(client, source.Name!, item.Value, source.TemplateId); // #261: the goal wants THIS item

			// Confirm the take: tell the client to remove the dragged slot (itemIndex) from the open window. AfterLootTake
			// only consumes/fades the bag once it is fully emptied, so a partial drag leaves it open with the rest.
			await _broadcaster.AfterLootTake(client, bagHandle, itemIndex);
		}
	}
}
