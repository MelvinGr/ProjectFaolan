using System.Threading.Tasks;
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
		// allowHashless permits a live add of an item that carries a key but no hash (claim items - #claims).
		Task<int?> GrantItemToBackpack(INetworkClient client, uint itemTemplateId, uint count = 1, bool allowHashless = false);

		// Push a live single-item add into a container (backpack 0x68, bank 0x69, ...). See GameServerListener.Outbound.cs SendAddItem.
		Task SendAddItem(INetworkClient client, ContainerKind containerType, uint itemPos, ProtoInventoryGameEntry entry, uint feedback = 0);

		// Push the wallet update (absolute balance in TIN) to the client. See GameServerListener.Outbound.cs SendUpdateCash.
		Task SendUpdateCash(INetworkClient client, ulong balanceTin, UpdateCashReason reason);
	}
}
