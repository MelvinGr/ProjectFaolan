using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Protobuf;

namespace Faolan.GameServer.Economy
{
	// The narrow set of cross-cutting OUTBOUND ops the economy/inventory collaborator services (ShopService / BankService) need to reach back to on the world listener: the wallet update and the two
	// live item grants. GameServerListener implements it (forwarding to its own Outbound builders), so those
	// services depend on this three-method surface instead of the whole listener god-class - the same decoupling
	// the per-op Func<> seams gave, collapsed into one named port now that several services share the same ops.
	// (Cross-connection LOOKUP seams like ClientForCharacter are a different concern and stay as their own seam.)
	public interface IEconomyOutbound
	{
		// Grant an item template into the first free backpack slot, returning the slot (or null when full).
		// `preferredSlot` is the backpack slot the player asked for - today only the loot window's drag target (#462).
		// It is a REQUEST, not a reservation: an occupied or out-of-range slot falls back to the first free one.
		//
		// `level` is the entry's wire level (ProtoAcgItem.level). It is a PER-TEMPLATE property - across all 8,644
		// inventory entries in the capture dumps, 0 templates carry two different levels - but our `Items.Level`
		// cannot supply it: it is 0 for 84% of sighted templates and, where populated, half of it is the wire value
		// divided by ten (#725). So a caller that HAS the captured level passes it, and everyone else keeps the 1
		// that every grant has always sent. Remove the parameter once #725 makes Items.Level authoritative.
		//
		// `creatorName` is the crafter, for a player-MADE item only (#727). It persists onto the inventory row and
		// rides the live add as ProtoInventoryGameEntry.creator_name, which is what draws the client's
		// "Created by <name>" tooltip line. Every other grant leaves it null: an item that was looted, bought or
		// handed in is not player-made, and a name there would be a false claim.
		// ⚠️ There is no `level` parameter, and there must not be one. It used to be here, defaulted to 1, and was
		// passed all the way down into the wire entry - where the item's level belongs to the TEMPLATE and is exactly
		// `Items.Level` (8,479 of 8,479 captured entries, zero exceptions). Two callers passed a level and the rest let
		// it default, so an item's level on the wire depended on which delivery path granted it. It is read from the
		// template now, once.
		Task<int?> GrantItemToBackpack(INetworkClient client, uint itemTemplateId, uint count = 1, int? preferredSlot = null, string? creatorName = null);

		// Push a live single-item add into a container (backpack 0x68, bank 0x69, ...). See GameServerListener.Outbound.cs SendAddItem.
		Task SendAddItem(INetworkClient client, ContainerKind containerType, uint itemPos, ProtoInventoryGameEntry entry, uint feedback = 0);

		// Push the wallet update (absolute balance in TIN) to the client. See GameServerListener.Outbound.cs SendUpdateCash.
		Task SendUpdateCash(INetworkClient client, ulong balanceTin, UpdateCashReason reason);

		// Take `count` of an item template OUT of the player's inventory, across containers and across split stacks,
		// returning how many were actually removed. The inverse of GrantItemToBackpack, and deliberately not
		// backpack-only: a quest item lives in the Quest container (0x71), not the bag, so RemoveItemFromBackpack
		// cannot reach it. Removes exactly `count` - a stack larger than what is asked for is decremented, never
		// cleared. See GameServerListener.Outbound.cs TakeItemsFromInventory.
		Task<uint> TakeItemsFromInventory(INetworkClient client, uint itemTemplateId, uint count);

		// A gem went into a socket (#754): DispatchAddToItemSlot naming the cell that RECEIVED it, then an ordinary
		// DispatchRemoveItem for the cell the gem left. Retail sends exactly these two and does NOT re-send the
		// socketed item's container entry, so the client updates the item in place from the first message.
		Task SendGemSocketed(INetworkClient client, ContainerKind container, uint targetSlot, ItemInstanceSocket socket, uint gemSlot);

		// The GUILD BANK vault's contents (#654) - pushed unasked as part of opening the banker, alongside the
		// personal bank. Its container's wire `instance` is the GUILD id, not a character's.
		Task SendGuildBankData(INetworkClient client, ProtoInventoryGame vault);

		// The echo that acknowledges a guild-bank item move: the six values the client asked for, back verbatim.
		Task SendGuildBankMove(INetworkClient client, GuildBankMove move);

		// HAND an existing item row to a character's backpack instead of minting one - the same choke point as
		// GrantItemToBackpack (live add, quest routing, collect goals), except the instance MOVES, so its crafter,
		// stack and socketed gems travel with it. Returns the bag slot, or null when the bag is full.
		Task<int?> TransferItemToCharacter(uint characterId, ItemInstance instance);
	}
}
