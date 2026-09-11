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
	/// <summary>
	///     The loot-window interaction flow: loot-all, single drag, cash, and close.
	/// </summary>
	/// <remarks>
	///     The client's four requests, decoded in <c>protocol_loot_interaction</c>:
	///     <code>
	///     TryLootAll   [loot typetag 0xC87A][bag handle]                 -> grant every bag item, then fade
	///     CloseLootBag [typetag][bag handle]                             -> drop the window without taking
	///     TryLootCash  [typetag][bag handle]                             -> credit the bag's coins to the wallet
	///     TryLootItem  [typetag][bag handle][display index]              -> grant the one dragged item
	///     </code>
	///     <para>
	///         <see cref="LootBroadcaster" /> owns the loot lifecycle - the corpse drop, the bag's fade and close, and
	///         (as its <c>Bags</c>) what each bag holds - while this service is the orchestration on top: it pulls items
	///         off the bag and grants them through the <see cref="IEconomyOutbound" /> port.
	///     </para>
	///     <para>
	///         The client does not add the item locally from the bag, so the server-side item grant is what actually
	///         delivers it; a persist-only take leaves the item un-takeable in the window.
	///     </para>
	/// </remarks>
	public sealed class LootService
	{
		private readonly IChatServerDispatcher _chat;
		private readonly ICharacterInventoryRepository _database;
		private readonly IEconomyOutbound _outbound;
		private readonly ILogger _logger;
		private readonly QuestProgressionService _questProgression;
		private readonly LootBroadcaster _loot;

		public LootService(LootBroadcaster loot, IEconomyOutbound outbound, ICharacterInventoryRepository database, QuestProgressionService questProgression,
			IChatServerDispatcher chat, ILogger logger)
		{
			_loot = loot;
			_outbound = outbound;
			_database = database;
			_questProgression = questProgression;
			_chat = chat;
			_logger = logger;
		}

		/// <summary>
		///     Grants one looted item, returning it to the bag when the grant fails - a full backpack, or an item the
		///     snapshot cannot build.
		/// </summary>
		/// <remarks>
		///     The bag hands each item over exactly once, so the caller must act on the result: discarding it destroys
		///     the loot and reports the take as a success.
		/// </remarks>
		/// <returns>Whether the player actually received the item.</returns>
		private async Task<bool> GrantLootedItem(INetworkClient client, uint bagHandle, uint itemTemplateId, int? position = null, int? preferredSlot = null)
		{
			if (await _outbound.GrantItemToBackpack(client, itemTemplateId, preferredSlot: preferredSlot) != null)
				return true;

			_loot.Bags.ReturnBagItem(client, bagHandle, itemTemplateId, position);
			if (client.Session.Character is { } character)
				await _chat.SendSystemMessage(character.Id, "You have no room for that - free a slot and loot it again.");

			return false;
		}

		/// <summary>The client closed the loot window without taking everything.</summary>
		public async Task HandleCloseLootBag(INetworkClient client, Rpc packet, FaolanStream body)
		{
			body.ReadUInt32(); // loot typetag (0xC87A)
			var bagHandle = body.ReadUInt32();
			await _loot.CloseLootBag(client, bagHandle);
		}

		/// <summary>
		///     The loot-all click: grants every item the bag carries into the backpack, persisting and pushing each one.
		/// </summary>
		/// <remarks>
		///     The bag hands its items over exactly once - <c>TakeBagItems</c> clears them - and coins ride
		///     <see cref="HandleLootCash" /> instead.
		/// </remarks>
		public async Task HandleLootAll(INetworkClient client, Rpc packet, FaolanStream body)
		{
			body.ReadUInt32(); // loot typetag (0xC87A)
			var bagHandle = body.ReadUInt32();

			// Read the bag's source NPC before taking: loot-all empties and consumes the bag, and the loot-quest goal
			// needs the source.
			var source = _loot.Bags.BagSource(client, bagHandle);

			var items = _loot.Bags.TakeBagItems(client, bagHandle);
			var granted = 0;
			foreach (var itemTemplateId in items)
			{
				if (!await GrantLootedItem(client, bagHandle, itemTemplateId))
					continue;

				granted++;

				// A loot goal is satisfied by receiving its quest item, so it advances per granted item rather than
				// once per loot-all. A bag without that item in it, or a take that bounced off a full backpack,
				// advances nothing.
				await _questProgression.AdvanceLootGoals(client, source.Name!, itemTemplateId, source.TemplateId);
			}

			if (items.Count > 0)
			{
				// Count what the player received, not what the bag handed over: the two differ when the backpack fills
				// up mid-loot, and the returned items keep the bag open, since AfterLootTake fades it only once it is
				// truly empty.
				_logger.LogInformation("Loot: granted {Granted} of {Count} item(s) from bag 0x{Bag:X8} to {Player}.", granted, items.Count, bagHandle, client.Session.Character?.Name);
			}

			// Confirm the take and, since loot-all empties it, consume and fade the bag. A loot-all removes every item
			// slot the bag handed over and retail says so in one message listing them all, the contiguous run
			// [0..n-1]; a shorter list leaves the client showing the slots it does not name.
			await _loot.AfterLootTake(client, bagHandle, [.. Enumerable.Range(0, items.Count).Select(i => (uint)i)]);
		}

		/// <summary>Credits the bag's coins to the player's wallet.</summary>
		/// <remarks>
		///     Coins auto-loot when the window opens: this is the client's first word about a bag, sent before the
		///     player takes anything, so it is not an action the player can decline. Across the corpus 67 of 70 bags
		///     open with it, and the canonical sequence is TryLootCash -> TryLootAll -> CloseLootBag (63 bags) or
		///     TryLootCash -> TryLootItem -> CloseLootBag (4). The server simply credits what the client asks for, but
		///     it is why a bag that held only coins is already empty by the time any item take happens.
		/// </remarks>
		public async Task HandleLootCash(INetworkClient client, Rpc packet, FaolanStream body)
		{
			body.ReadUInt32(); // loot typetag (0xC87A)
			var bagHandle = body.ReadUInt32();

			var character = client.Session.Character;
			var cash = _loot.Bags.TakeLootCash(client, bagHandle);
			if (cash > 0 && character != null)
			{
				character.Money += cash;
				await _database.SetCharacterMoney(character.Id, character.Money);
				await _outbound.SendUpdateCash(client, character.Money, UpdateCashReason.Gain);
				// Retail follows the balance update with the bag-window removal of the money row.
				await LootBroadcaster.AfterLootCash(client, bagHandle, cash);
				_logger.LogInformation("Loot: {Player} looted {Cash} tin from bag 0x{Bag:X8} (wallet {Balance}).", character.Name, cash, bagHandle, character.Money);
			}
			else
				_logger.LogDebug("TryLootCash for bag 0x{Bag:X8}: no coins to grant.", bagHandle);
		}

		/// <summary>A single drag of one item out of the loot window.</summary>
		/// <remarks>
		///     The client's own signature is
		///     <c>TryLootItem(const ID32&amp; i_cLootBagID, int i_nItemPos, const ID32&amp; i_cCharID, int
		///     i_nDesiredPosition)</c> (docs/re/aoc-client-rpc-methods.json). <c>i_nItemPos</c> runs over the bag's
		///     display indices while <c>i_nDesiredPosition</c> is the backpack slot the player dropped the item on; the
		///     two captured multi-loot sequences show the latter tracking the player's own filling backpack
		///     (0,1,2,...,5,6). The desired slot is honoured as a request only: an occupied or out-of-range slot falls
		///     back to the first free one, because a drag onto a full slot must put the item somewhere rather than
		///     failing or overwriting what is there.
		/// </remarks>
		public async Task HandleLootItem(INetworkClient client, Rpc packet, FaolanStream body)
		{
			body.ReadUInt32(); // loot typetag (0xC87A)
			var bagHandle = body.ReadUInt32();
			var itemIndex = body.ReadUInt32(); // i_nItemPos - which item (display index) the player dragged
			body.ReadUInt32(); // EntityClass.Character (0xC350)
			var looterHandle = body.ReadUInt32(); // i_cCharID - the looter; we key off the session instead
			var desiredPosition = body.ReadUInt32();

			_logger.LogDebug("TryLootItem: bag {Bag:X8} index {Index} -> looter {Looter:X8} desired slot {Slot}.", bagHandle, itemIndex, looterHandle, desiredPosition);

			var source = _loot.Bags.BagSource(client, bagHandle);
			var item = _loot.Bags.TakeBagItem(client, bagHandle, (int)itemIndex);
			if (item != null && await GrantLootedItem(client, bagHandle, item.Value, (int)itemIndex, (int)desiredPosition))
				await _questProgression.AdvanceLootGoals(client, source.Name!, item.Value, source.TemplateId); // The goal wants this one item

			// Confirm the take: tell the client to remove the dragged slot from the open window. AfterLootTake consumes
			// and fades the bag only once it is fully emptied, so a partial drag leaves it open with the rest.
			await _loot.AfterLootTake(client, bagHandle, itemIndex);
		}
	}
}
