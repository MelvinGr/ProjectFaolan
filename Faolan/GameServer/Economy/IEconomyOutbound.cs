using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Protobuf;

namespace Faolan.GameServer.Economy
{
	/// <summary>
	///     The narrow set of cross-cutting outbound ops the economy and inventory services share - the wallet update
	///     and the live item grants - so <see cref="ShopService" />, <see cref="BankService" /> and their neighbours
	///     depend on this surface rather than on the listener.
	/// </summary>
	/// <remarks>
	///     Cross-connection lookup seams such as <c>ClientForCharacter</c> are a different concern and stay as their
	///     own seam.
	/// </remarks>
	public interface IEconomyOutbound
	{
		/// <summary>
		///     Grant an item template into the first free backpack slot.
		/// </summary>
		/// <param name="preferredSlot">
		///     The backpack slot the player asked for - today only the loot window's drag target. It is a request, not
		///     a reservation: an occupied or out-of-range slot falls back to the first free one.
		/// </param>
		/// <param name="creatorName">
		///     The crafter, for a player-made item only. It persists onto the inventory row and rides the live add as
		///     <c>ProtoInventoryGameEntry.creator_name</c>, which draws the client's "Created by &lt;name&gt;" tooltip
		///     line. Every other grant leaves it null: an item that was looted, bought or handed in is not
		///     player-made, and a name there would be a false claim.
		/// </param>
		/// <returns>The slot, or null when the backpack is full.</returns>
		/// <remarks>
		///     There is deliberately no <c>level</c> parameter. An item's level belongs to the template and is exactly
		///     <c>Items.Level</c> (8,479 of 8,479 captured entries, no exceptions), so it is read from the template
		///     once rather than passed per delivery path.
		/// </remarks>
		Task<int?> GrantItemToBackpack(INetworkClient client, uint itemTemplateId, uint count = 1, int? preferredSlot = null, string? creatorName = null);

		/// <summary>
		///     Push a live single-item add into a container (backpack 0x68, bank 0x69, ...).
		/// </summary>
		Task SendAddItem(INetworkClient client, ContainerKind containerType, uint itemPos, ProtoInventoryGameEntry entry, uint feedback = 0);

		/// <summary>Push the wallet update - the absolute balance in tin - to the client.</summary>
		Task SendUpdateCash(INetworkClient client, ulong balanceTin, UpdateCashReason reason);

		/// <summary>
		///     Take <paramref name="count" /> of an item template out of the player's inventory, across containers and
		///     across split stacks.
		/// </summary>
		/// <returns>How many were actually removed.</returns>
		/// <remarks>
		///     The inverse of <see cref="GrantItemToBackpack" />, and deliberately not backpack-only: a quest item
		///     lives in the Quest container (0x71) rather than the bag. It removes exactly
		///     <paramref name="count" /> - a larger stack is decremented, never cleared.
		/// </remarks>
		Task<uint> TakeItemsFromInventory(INetworkClient client, uint itemTemplateId, uint count);

		/// <summary>
		///     A gem went into a socket: DispatchAddToItemSlot naming the cell that received it, then an ordinary
		///     DispatchRemoveItem for the cell the gem left.
		/// </summary>
		/// <remarks>
		///     Retail sends exactly these two and does not re-send the socketed item's container entry, so the client
		///     updates the item in place from the first message.
		/// </remarks>
		Task SendGemSocketed(INetworkClient client, ContainerKind container, uint targetSlot, ItemInstanceSocket socket, uint gemSlot);

		/// <summary>
		///     Closes a craft: <c>DispatchTradeskillComplete</c>, body = the recipe id.
		/// </summary>
		/// <remarks>
		///     Reached through the port because the craft does not complete in the inbound handler: the world tick runs
		///     it out of <see cref="TradeskillService" /> five seconds later, and the tick has no listener.
		/// </remarks>
		Task SendTradeskillComplete(INetworkClient client, uint recipeId);

		/// <summary>
		///     The guild-bank vault's contents, pushed unasked as part of opening the banker, alongside the personal
		///     bank. Its container's wire <c>instance</c> is the guild id, not a character's.
		/// </summary>
		Task SendGuildBankData(INetworkClient client, ProtoInventoryGame vault);

		/// <summary>
		///     The echo that acknowledges a guild-bank item move: the six values the client asked for, back verbatim.
		/// </summary>
		Task SendGuildBankMove(INetworkClient client, GuildBankMove move);

		/// <summary>
		///     Hand an existing item row to a character's backpack instead of minting one - the same choke point as
		///     <see cref="GrantItemToBackpack" /> (live add, quest routing, collect goals), except the instance moves,
		///     so its crafter, stack and socketed gems travel with it.
		/// </summary>
		/// <returns>The bag slot, or null when the bag is full.</returns>
		Task<int?> TransferItemToCharacter(uint characterId, ItemInstance instance);
	}
}
