using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.GameServer.World;

namespace Faolan.GameServer
{
	// The wire side of local travel (#506) - ITravelOutbound. The decisions live in World/TravelService.cs.
	public partial class GameServerListener
	{
		// The localized-format key the travel confirmation box carries. Retail's boxes each ship a different 20-char
		// key (`%z&#36;n_zw5ComNiHcqoW4j` for Ymir's Pass, `rtE^QH#90Dh9&#92;(YR+b,K` for Field of the Dead), so it is
		// per-STRING rather than per-message-type - it is the client's own string-table addressing, and we cannot
		// synthesise the right one for an arbitrary map. One captured key is reused for every destination: the client
		// resolves the text from `id`+`category`, which ARE correct, so the box names the right map.
		// ⚠️ [V0] Whether a wrong key degrades the rendering (a fallback string instead of the map name) is unverified
		// in-client - that is exactly what the needs-testing pass on #506 has to look at.
		private const string TravelConfirmKey = "%z&#36;n_zw5ComNiHcqoW4j";

		// The scry confirmation box (DispatchScryShowMessageBox 0x205E) naming the destination map. Framed on the same
		// GameCharAgent -> GameCharInterface conversation as the sibling scry/stock-phrase dispatches.
		Task ITravelOutbound.SendTravelConfirm(INetworkClient client, uint destinationMapId, uint threadCtxId, ulong cookie)
		{
			var id = client.Session.Character.Id;
			return client.SendAsync(new Rpc(Endpoints.Endpoint(Endpoints.GameCharAgentType, 0x08), Endpoints.Endpoint(Endpoints.GameCharInterfaceType, id),
				GameCharInterfaceMethods.DispatchScryShowMessageBox,
				ScryShowMessageBoxCodec.Build(destinationMapId, TravelConfirmKey, threadCtxId, cookie)));
		}

		Task ITravelOutbound.SendTravelRefusal(INetworkClient client, string reason)
		{
			return _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id, ChatMarkup.Color("red", reason));
		}

		Task ITravelOutbound.SendTravelFeeCharged(INetworkClient client, ulong balanceTin)
		{
			return SendUpdateCash(client, balanceTin, UpdateCashReason.Spend);
		}

		// A cross-map trip is the ordinary changemap handoff - the same path GM .teleport and the recalls take, so
		// travel gets the persisted transform, the loading screen and the re-entry latch for free.
		Task ITravelOutbound.TravelToMap(uint characterId, uint destinationMapId, Vector3? arrival)
		{
			return DoTeleport(characterId, destinationMapId, arrival);
		}

		// A SAME-MAP trip: relocate in place with no loading screen. Retail's own wire path for this is the revive
		// teleport (IntendedTeleport + n3Teleport on the current playfield token), so it is reused verbatim rather than
		// running a changemap the client would answer with a full reload.
		async Task ITravelOutbound.RelocateOnSameMap(INetworkClient client, Vector3 arrival)
		{
			var character = client.Session.Character;
			var entity = _simulation.PlayerWorldEntity(client);

			character.Position = arrival;
			if (entity != null)
				entity.Position = arrival;

			await Database.SaveCharacterTransform(character.Id, arrival, character.Rotation, character.MapId);
			await ((IWorldOutbound)this).SendReviveDestination(client, character.Id, character.MapId, arrival, 0f, 0f);
		}
	}
}
