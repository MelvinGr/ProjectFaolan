using System.Collections.Concurrent;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Constants;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.GameServer.CreateFlow;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	// The CHANGEMAP handoff: persist the destination, announce it on the live connection, then advertise the world
	// server so the client reconnects and does a fresh world entry there. Every teleport in the server runs through
	// it - GM `.teleport`, the recalls, the travel box (#309), a zone-line door, a feat's world reload, and
	// ITravelOutbound's cross-map trip (#506).
	//
	// It also owns the two latches that have to survive the handoff, because the re-entry arrives on a NEW
	// connection: the re-entry mark (which arms the post-0x205F follow-up - #28) and the epic-difficulty pick
	// (#309). The listener's Hello / world-entry path consumes them through the four methods at the bottom.
	//
	// Contrast WorldSimulation.RelocateSameMap, which moves a player WITHIN the loaded playfield with no handoff and
	// no loading screen.
	public sealed class TeleportService
	{
		// Characters whose armed teleport chose the EPIC difficulty (#309): set in DoTeleport beside the reentry
		// mark below, consumed by the reconnect's world entry - the difficulty must survive the handoff because the
		// entry happens on a NEW connection. Absent = normal, so nothing is stored for the common case.
		private readonly ConcurrentDictionary<uint, byte> _pendingEpicEntry = new ConcurrentDictionary<uint, byte>();

		// Client instances awaiting a TELEPORT/changemap re-entry: added in DoTeleport before the 0x2002 handoff and
		// consumed on the reconnect Hello. A direct GameServer->GameServer changemap reconnect carries NO 0x205F (the
		// retail changemap capture confirms it - the flag is normally armed by the client's 0x205F at full login), so
		// we arm HasPendingPost205FFollowUp ourselves there. Without it the world burst lands but 0x2024 ReadyForPlay-
		// Screen never fires and the client sits on the loading screen forever (the totem-recall hang, Gitea #28).
		// The old + reconnect connections are different sessions, which is why the latch lives on the SERVICE rather
		// than on either session. ConcurrentDictionary-as-set for thread safety.
		private readonly ConcurrentDictionary<uint, byte> _pendingTeleportReentry = new ConcurrentDictionary<uint, byte>();

		private readonly WorldBroadcaster _broadcaster;
		private readonly IChatServerDispatcher _chat;
		private readonly ConnectionRegistry _connections;
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;

		public TeleportService(ConnectionRegistry connections, IDatabaseRepository database, WorldBroadcaster broadcaster, IChatServerDispatcher chat, ILogger logger)
		{
			_connections = connections;
			_database = database;
			_broadcaster = broadcaster;
			_chat = chat;
			_logger = logger;
		}

		// toPosition: an explicit destination within the map (e.g. a bind/recall point); null = the map's spawn point.
		// `epic` is the difficulty the player picked at the travel box (#309): it rides _pendingEpicEntry across the
		// handoff (the reconnect's world entry is a new connection) and selects the destination's class word on the
		// prep packets below.
		public async Task DoTeleport(uint characterId, uint mapId, Vector3? toPosition = null, bool epic = false)
		{
			var client = _connections.ForCharacter(characterId);
			if (client?.Session.Character == null)
				return;

			var map = await _database.GetMap(mapId);
			if (map == null)
			{
				await _chat.SendSystemMessage(characterId, ChatMarkup.Color("red", $"Unknown map id: {mapId}"));
				return;
			}

			// Destination: an explicit point (bind/recall/coords) or the map's default spawn (the AlwaysKnown starter, 			// else any respawn point - GetDefaultSpawn). A map with no points defined can't resolve a spawn, so refuse
			// rather than dump the player at the origin. Persist BEFORE the handoff so the reconnect's world entry
			// (existing-character Hello -> SendWorldEntry, which loads Character.MapId) lands at the right spot.
			Vector3 position;
			Quaternion rotation;
			if (toPosition.HasValue)
			{
				position = toPosition.Value;
				rotation = Quaternion.Identity;
			}
			else
			{
				var spawn = await _database.GetDefaultSpawn(map.Id);
				if (spawn == null)
				{
					await _chat.SendSystemMessage(characterId,
						ChatMarkup.Color("red", $"No spawn point defined for map {(string.IsNullOrEmpty(map.Name) ? map.Id.ToString() : $"{map.Name} ({map.Id})")}. Give explicit coordinates: .teleport <x> <y> <z>."));
					return;
				}

				position = spawn.Position;
				rotation = spawn.Rotation;
			}

			client.Session.Character.MapId = map.Id;
			client.Session.Character.Position = position;
			client.Session.Character.Rotation = rotation;
			await _database.SaveCharacterTransform(client.Session.Character.Id, position, rotation, map.Id);

			var mapLabel = string.IsNullOrEmpty(map.Name)
				? map.Id.ToString()
				: $"{map.Name} ({map.Id})";
			await _chat.SendSystemMessage(characterId, $"Teleporting to {mapLabel}...");

			// Reproduce the retail change-map sequence (decoded from both changemap captures, consistent):
			// on the live connection, announce the destination - 0x10D27BC0 ("begin teleport to map N") then
			// 0x5A32F0D7 (destination map + spawn position) - THEN the 0x2002 handoff advert, after which the
			// client reconnects and does a fresh world entry on the new map. All three ride the handoff
			// conversation (0xb171ce13 -> 0x6c67c147 = SendWorldHandoff's sender/receiver).
			// Mark this client as a teleport re-entry so the reconnect Hello arms the post-0x205F follow-up (the
			// changemap reconnect carries no 0x205F, so 0x2024 ReadyForPlayScreen would otherwise never fire and the
			// loading screen would hang - #28). Consumed in the GameServer Hello existing-character branch.
			_pendingTeleportReentry.TryAdd(client.Session.Character.Id, 0);
			if (epic)
				_pendingEpicEntry.TryAdd(client.Session.Character.Id, 0);
			else
				_pendingEpicEntry.TryRemove(client.Session.Character.Id, out _);

			// We've just persisted the destination position above; stop THIS (old) connection's imminent disconnect
			// from saving its stale pre-teleport live position back over it (ClientDisconnected does a fire-and-forget
			// SavePlayerState that would otherwise clobber the bind/destination - #28).
			client.Session.IsTeleportingOut = true;

			await SendChangeMapPrep(client, map.Id, position, epic: epic);
			await SendWorldHandoff(client);
		}

		// The change-map prep IIR bodies (begin + destination announce) the retail teleport sends before the 0x2002
		// handoff, built by the named TeleportCodec (#219, World/TeleportCodec.cs). In-client verification pending.
		public async Task SendChangeMapPrep(INetworkClient client, uint destMapId, Vector3 position, uint destDescriptor = 0x00000014u, bool epic = false)
		{
			var instance = client.Session.Character.Id;
			// Source the destination playfield token from the destination map's burst so the announce matches the token
			// the reconnect's world-entry will stamp (#219 / #211) - it is per-playfield-instance, not a constant.
			var destToken = await _broadcaster.ResolveMapPlayfieldToken(destMapId);
			// #792: the zero-length 0x1B first - retail precedes EVERY IntendedTeleport with it, 102 of 102 across the
			// corpus. The client discards it (its handler is an empty override); we send it because retail does.
			await GameServerListener.SendTeleportPending(client);
			foreach (var body in new[] { TeleportCodec.Build(instance, destMapId, epic), TeleportCodec.Build(instance, destMapId, position, destToken, destDescriptor, epic) })
			{
				await client.SendAsync(new Rpc(CreateFlowService.MorphAckSender, CreateFlowService.Receiver(client), GameInterfaceMethods.DispatchPassBlob, s => s.WriteArrayUInt32Length(body)));
			}
		}

		public async Task SendWorldHandoff(INetworkClient client)
		{
			var realm = await _database.GetRealm(client.Session.Character.RealmId);
			var address = $"{realm!.IpAddress}:{realm.Port}";

			await client.SendAsync(new Rpc(CreateFlowService.MorphAckSender, CreateFlowService.Receiver(client), GameInterfaceMethods.DispatchRedirectToServer,
				b => b.WriteByte(0x00).WriteStringByteLength(address)));

			_logger.LogInformation("Sent enter-world handoff (0x2002) advertising world server {Address}.", address);
		}

		// --- the two handoff latches. Armed in DoTeleport above, consumed by the listener's reconnect path.

		/// <summary>Is a teleport re-entry armed for this client instance? A PEEK - the login branch consumes it.</summary>
		public bool IsReentryPending(uint clientInstance)
		{
			return _pendingTeleportReentry.ContainsKey(clientInstance);
		}

		/// <summary>Consume the teleport re-entry mark, returning whether one was armed.</summary>
		public bool ConsumeReentry(uint clientInstance)
		{
			return _pendingTeleportReentry.TryRemove(clientInstance, out _);
		}

		/// <summary>Consume the epic-difficulty pick, returning whether the armed teleport chose epic.</summary>
		public bool ConsumeEpicEntry(uint characterId)
		{
			return _pendingEpicEntry.TryRemove(characterId, out _);
		}
	}
}
