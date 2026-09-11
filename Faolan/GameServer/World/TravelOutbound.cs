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
using Faolan.GameServer.Economy;
using Faolan.GameServer.Wire;

using System.Threading;

using Faolan.Core.Database.Tables;

using Faolan.Core.Network.Codecs.Dialogue;

using Faolan.Core.Crypt;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     The wire side of local travel - <see cref="ITravelOutbound" />. The decisions live in
	///     <see cref="TravelService" />.
	/// </summary>
	/// <remarks>
	///     Every op here is either a send or a forward to a service that owns the act - the changemap handoff, the
	///     same-map relocate, the wallet push - which is why it is its own class rather than a facade on the listener.
	/// </remarks>
	public sealed class TravelOutbound : ITravelOutbound
	{
		private readonly IChatServerDispatcher _chat;
		private readonly IEconomyOutbound _economy;
		private readonly WorldSimulation _simulation;
		private readonly TeleportService _teleport;

		private readonly ILogger _logger;

		public TravelOutbound(IChatServerDispatcher chat, IEconomyOutbound economy, TeleportService teleport, WorldSimulation simulation, ILogger logger)
		{
			_logger = logger;
			_chat = chat;
			_economy = economy;
			_teleport = teleport;
			_simulation = simulation;
		}

		public Task SendTravelRefusal(INetworkClient client, string reason)
		{
			return _chat.SendSystemMessage(client.Session.Character.Id, ChatMarkup.Color("red", reason));
		}

		public Task SendTravelFeeCharged(INetworkClient client, ulong balanceTin)
		{
			return _economy.SendUpdateCash(client, balanceTin, UpdateCashReason.Spend);
		}

		// A cross-map trip is the ordinary changemap handoff - the same path the GM teleport and the recalls take - so
		// travel gets the persisted transform, the loading screen and the re-entry latch for free.
		public Task TravelToMap(uint characterId, uint destinationMapId, Vector3? arrival)
		{
			return _teleport.DoTeleport(characterId, destinationMapId, arrival);
		}

		// A same-map trip: relocate in place with no loading screen - retail's `Local=1` trip. The wire pair is the one
		// the revive uses (IntendedTeleport on the live playfield token, then n3Teleport carrying the destination),
		// which is what the captured wagoner trip sends at t=781.5, so it is reused rather than running a changemap
		// the client would answer with a full reload.
		public Task RelocateOnSameMap(INetworkClient client, Vector3 arrival)
		{
			return _simulation.RelocateSameMap(client, arrival);
		}

		/// <summary>
		///     The two records retail puts between the destination pick and the teleport, in retail's own order: the
		///     trip's own GameActionListIIR, then the casterless Racer buff.
		/// </summary>
		/// <remarks>
		///     The GameActionListIIR carries one GameActionData element of type 0xD00D
		///     (StartServerSideScryScriptWithParams) - the server ran its travel script - whose <c>ScriptParams</c>
		///     states the whole trip as "xpos=..;ypos=..;zpos=..;PlayFieldID=..;Local=1". The Racer buff is half a
		///     second long, on the travelling player. Neither moves the player: the IntendedTeleport and n3Teleport
		///     pair behind them does that. This is fidelity - the client is told a trip is happening rather than being
		///     silently relocated. The element's <c>field</c> is retail's own <c>GameActionData::s_nInstance</c>
		///     counter, so this allocates its own.
		/// </remarks>
		public async Task SendTripDeparture(INetworkClient client, uint destinationMapId, Vector3 arrival, bool sameMap)
		{
			var self = client.Session.Character.Id;
			var trip = sameMap
				? TravelActions.Local(destinationMapId, arrival.X, arrival.Y, arrival.Z)
				: TravelActions.CrossPlayfield(destinationMapId, arrival.X, arrival.Y, arrival.Z);

			var action = new GameActionListIIR(self, TravelActionCodec.TypeId, GameActionInstanceId.Next(), TravelActionCodec.Build(trip));
			await SendWorldRecord(client, GameActionListCodec.Build(action));

			// Casterless: retail writes a zero entity-class word where the caster reference would be, so this does not
			// go through SendMagicBuffIir - that path is for buffs with a caster, and it would also register a
			// half-second buff in the tracker and send an end twin retail only sends on a cross-map trip.
			var racer = MagicBuffCodec.Build(new MagicBuffIIR(self, 0x00, MagicBuffIds.BaseOf(TravelCatalog.RacerSpell), MagicBuffIds.FormOf(TravelCatalog.RacerSpell), null,
				MagicBuffDataCodec.Build(TravelCatalog.RacerPayload(BuffService.CasterLevel(client)))));

			await SendWorldRecord(client, racer);
		}

		// A `{teleportplayer}` directive may state only X and Z, so the arrival height comes off the baked terrain.
		// Null when none is loaded for that map - the terrain directory is unset, or the map has no baked file - and
		// the service then falls back to the destination's default spawn rather than guessing a height.
		public float? GroundHeight(uint mapId, float x, float z)
		{
			return _simulation.Terrain?.GroundHeight(mapId, x, z);
		}

		private static Task SendWorldRecord(INetworkClient client, byte[] record)
		{
			return client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(record)));
		}
		// Hands out the cookie of each travel confirmation box. Retail's is a script-context pointer; this one only has
		// to be unique among the boxes a client might have outstanding, which a per-host counter is.
		private long _travelConfirmCookie;

		/// <summary>
		///     Raises retail's "Do you want to travel to &lt;map&gt;?" box for a zone-line door, and remembers the trip
		///     it is confirming.
		/// </summary>
		/// <remarks>
		///     The (threadCtxId, cookie) pair is retail's script-thread handle, and the client only echoes it, so these
		///     are a correlation token rather than a real thread: the character id plus a per-send counter, unique
		///     within a session, which is all the answer has to match. Retail's own sender endpoint instance varies per
		///     box across the corpus (0x54, 0x56 to 0x5A - it is whichever scry agent owns the script), so 0x58 here is
		///     a default rather than a captured constant.
		/// </remarks>
		public async Task SendTravelConfirmation(INetworkClient client, Map destination, Vector3? arrival)
		{
			var threadCtxId = client.Session.Character.Id;
			var cookie = (ulong)Interlocked.Increment(ref _travelConfirmCookie);
			client.Session.PendingTravelConfirm = new PendingTravelConfirm(threadCtxId, cookie, destination.Id, arrival);

			await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x58), client.Endpoint(RpcInterfaces.GameCharInterfaceType),
				GameCharInterfaceMethods.DispatchScryShowMessageBox,
				ScryShowMessageBoxCodec.Build(destination.Id, LdbKey.ToWire(destination.TravelConfirmKey), threadCtxId, cookie)));

			_logger.LogInformation("Travel confirm for '{Map}' ({MapId}) sent to character {CharacterId} (cookie 0x{Cookie:X16}).", destination.Name, destination.Id,
				client.Session.Character.Id, cookie);
		}

		/// <summary>
		///     The ACG instance bind box: the confirmation retail raises instead of teleporting when the destination is
		///     a personal instance.
		/// </summary>
		/// <remarks>
		///     The client answers with RequestACGTeleport, or with nothing at all if the player declines, so the offer
		///     is recorded on the session for the handler to check against. The proxy is built for the destination: its
		///     class word is what marks the playfield as ACG, and it is echoed back verbatim in the request. The
		///     instance token is 0, because per-player instance tokens are not minted and the capture's token is that
		///     session's rather than a per-map constant.
		/// </remarks>
		public async Task SendAcgTeleportChoice(INetworkClient client, uint destinationMapId, uint playfieldClass, Vector3? arrival)
		{
			client.Session.PendingAcgTeleport = new PendingAcgTeleport(destinationMapId, arrival);

			var proxy = PlayfieldProxy.ForMap(destinationMapId) with { ClassWord = playfieldClass };
			await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x56), client.Endpoint(RpcInterfaces.GameCharInterfaceType),
				GameCharInterfaceMethods.DispatchACGTeleportChoice,
				AcgTeleportChoiceCodec.Build(new AcgTeleportChoice(client.Session.Character.Id, proxy))));

			_logger.LogInformation("ACG bind box for map {MapId} sent to character {CharacterId}.", destinationMapId, client.Session.Character.Id);
		}
	}
}
