using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;

namespace Faolan.GameServer
{
	// The wire side of local travel (#506) - ITravelOutbound. The decisions live in World/TravelService.cs.
	public partial class GameServerListener
	{
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

		// A SAME-MAP trip: relocate in place with no loading screen - retail's `Local=1` trip. The wire pair is the one
		// the revive already uses (IntendedTeleport on the live playfield token, then n3Teleport carrying the
		// destination), which is exactly what the captured wagoner trip sends at t=781.5, so it is reused rather than
		// running a changemap the client would answer with a full reload.
		Task ITravelOutbound.RelocateOnSameMap(INetworkClient client, Vector3 arrival)
		{
			return _simulation.RelocateSameMap(client, arrival);
		}

		// #636 - the two records retail puts between the destination pick and the teleport, in retail's own order:
		//
		//   1. the trip's own GameActionListIIR. One GameActionData element of type 0xD00D
		//      (StartServerSideScryScriptWithParams): the server ran its travel script, and the element's
		//      `ScriptParams` states the whole trip - "xpos=..;ypos=..;zpos=..;PlayFieldID=..;Local=1".
		//   2. the casterless *Racer* buff, half a second long, on the travelling player.
		//
		// Neither moves the player - the IntendedTeleport/n3Teleport pair behind them does, and that pair is
		// in-client verified - so this is fidelity: the client is told a trip is happening rather than being silently
		// relocated. It stayed unsent until the element's `field` was decoded, which read as a per-trip value derived
		// from nothing we hold; it is retail's own `GameActionData::s_nInstance` counter, so we allocate our own.
		async Task ITravelOutbound.SendTripDeparture(INetworkClient client, uint destinationMapId, Vector3 arrival, bool sameMap)
		{
			var self = client.Session.Character.Id;
			var trip = sameMap
				? TravelActions.Local(destinationMapId, arrival.X, arrival.Y, arrival.Z)
				: TravelActions.CrossPlayfield(destinationMapId, arrival.X, arrival.Y, arrival.Z);

			var action = new GameActionListIIR(self, TravelActionCodec.TypeId, GameActionInstanceId.Next(), TravelActionCodec.Build(trip));
			await SendWorldRecord(client, GameActionListCodec.Build(action));

			// Casterless: retail writes a zero entity-class word where the caster reference would be, so it does NOT
			// go through SendMagicBuffIir - that path is for buffs with a caster, and it would also register a
			// half-second buff in the tracker and send an end twin retail only sends on a cross-map trip.
			var racer = MagicBuffCodec.Build(new MagicBuffIIR(self, 0x00, MagicBuffIds.BaseOf(TravelCatalog.RacerSpell), MagicBuffIds.FormOf(TravelCatalog.RacerSpell), null,
				MagicBuffDataCodec.Build(TravelCatalog.RacerPayload(BuffService.CasterLevel(client)))));

			await SendWorldRecord(client, racer);
		}

		private static Task SendWorldRecord(INetworkClient client, byte[] record)
		{
			return client.SendAsync(new Rpc(WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(record)));
		}

		// #635: a `{teleportplayer}` directive may state only X and Z, so the arrival height comes off the baked
		// terrain. Null when none is loaded for that map (TerrainDir unset, or the map has no baked file) - the
		// service then falls back to the destination's default spawn rather than guessing a height.
		float? ITravelOutbound.GroundHeight(uint mapId, float x, float z)
		{
			return _simulation.Terrain?.GroundHeight(mapId, x, z);
		}
	}
}
