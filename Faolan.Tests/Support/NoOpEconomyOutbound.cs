using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Economy;

namespace Faolan.Tests.Support
{
	// `IEconomyOutbound` with every member a no-op, for the service tests that only care about one or two of them.
	//
	// It exists because the port is a growing surface and each addition broke every test double at once: three
	// classes were implementing all of it by hand to record two calls each, so `SendGemSocketed` (#754) and then
	// `SendGuildBankData`/`SendGuildBankMove` (#654) each failed three unrelated test classes that had no opinion
	// about them. Overriding what a test asserts on, and inheriting silence for the rest, is what makes the port
	// extensible without a fan-out of edits.
	//
	// ⚠️ Deliberately no-ops rather than throws. A test that wires this up is exercising a SERVICE, and a service
	// legitimately calls outbound ops the test is not about - the wallet update on a craft, say. Throwing would
	// make every such test assert on the whole port instead of on its subject.
	public class NoOpEconomyOutbound : IEconomyOutbound
	{
		public virtual Task<int?> GrantItemToBackpack(INetworkClient client, uint itemTemplateId, uint count = 1, int? preferredSlot = null, string? creatorName = null)
		{
			return Task.FromResult<int?>(0);
		}

		public virtual Task SendAddItem(INetworkClient client, ContainerKind containerType, uint itemPos, ProtoInventoryGameEntry entry, uint feedback = 0)
		{
			return Task.CompletedTask;
		}

		public virtual Task SendGemSocketed(INetworkClient client, ContainerKind container, uint targetSlot, ItemInstanceSocket socket, uint gemSlot)
		{
			return Task.CompletedTask;
		}

		public virtual Task SendGuildBankData(INetworkClient client, ProtoInventoryGame vault)
		{
			return Task.CompletedTask;
		}

		public virtual Task SendGuildBankMove(INetworkClient client, GuildBankMove move)
		{
			return Task.CompletedTask;
		}

		public virtual Task SendUpdateCash(INetworkClient client, ulong balanceTin, UpdateCashReason reason)
		{
			return Task.CompletedTask;
		}

		public virtual Task<int?> TransferItemToCharacter(uint characterId, ItemInstance instance)
		{
			return Task.FromResult<int?>(0);
		}

		public virtual Task<uint> TakeItemsFromInventory(INetworkClient client, uint itemTemplateId, uint count)
		{
			return Task.FromResult(count);
		}
	}
}
