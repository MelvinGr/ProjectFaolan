using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.GameServer.CreateFlow;
using Faolan.GameServer.Wire;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     The changemap handoff: persist the destination, announce it on the live connection, then advertise the world
	///     server so the client reconnects and does a fresh world entry there.
	/// </summary>
	/// <remarks>
	///     Every teleport runs through it: the GM <c>.teleport</c>, the recalls, the travel box, a zone-line door, a
	///     feat's world reload, and <see cref="ITravelOutbound" />'s cross-map trip.
	///     <para>
	///         It also owns the two latches that have to survive the handoff, because the re-entry arrives on a new
	///         connection: the re-entry mark and the epic-difficulty pick. The listener's Hello and world-entry path
	///         consumes them through the methods at the bottom.
	///     </para>
	///     <para>
	///         Contrast <c>WorldSimulation.RelocateSameMap</c>, which moves a player within the loaded playfield with
	///         no handoff and no loading screen.
	///     </para>
	/// </remarks>
	public sealed class TeleportService
	{
		// Characters whose armed teleport chose the epic difficulty: set in DoTeleport beside the re-entry mark below
		// and consumed by the reconnect's world entry, because the difficulty has to survive the handoff. An absent
		// entry means normal, so nothing is stored for the common case.
		private readonly ConcurrentDictionary<uint, byte> _pendingEpicEntry = new ConcurrentDictionary<uint, byte>();

		// The live buffs of characters mid-handoff; see CarriedBuffHold for what it is and why.
		private readonly CarriedBuffHold _carriedBuffs = new CarriedBuffHold();

		// Client instances awaiting a changemap re-entry: added in DoTeleport before the handoff advert and consumed on
		// the reconnect Hello. A direct GameServer-to-GameServer changemap reconnect carries no 0x205F - the flag is
		// normally armed by the client's 0x205F at full login - so HasPendingPost205FFollowUp is armed here instead.
		// Without it the world burst lands but ReadyForPlayScreen never fires and the client sits on the loading screen
		// for ever. The old and reconnect connections are different sessions, which is why the latch lives on the
		// service rather than on either session; the dictionary is a set, used for thread safety.
		private readonly ConcurrentDictionary<uint, byte> _pendingTeleportReentry = new ConcurrentDictionary<uint, byte>();

		private readonly WorldBroadcaster _broadcaster;
		private readonly IChatServerDispatcher _chat;
		private readonly ConnectionRegistry _connections;
		private readonly ICharacterRepository _characters;
		private readonly IRealmRepository _realms;
		private readonly IWorldRepository _world;
		private readonly ILogger _logger;

		public TeleportService(ConnectionRegistry connections, ICharacterRepository characters, IRealmRepository realms, IWorldRepository world, WorldBroadcaster broadcaster, IChatServerDispatcher chat, ILogger logger)
		{
			_connections = connections;
			_characters = characters;
			_realms = realms;
			_world = world;
			_broadcaster = broadcaster;
			_chat = chat;
			_logger = logger;
		}

		/// <param name="toPosition">
		///     An explicit destination within the map, such as a bind or recall point; null selects the map's spawn
		///     point.
		/// </param>
		/// <param name="epic">
		///     The difficulty the player picked at the travel box. It rides across the handoff, because the reconnect's
		///     world entry is a new connection, and selects the destination's class word on the prep packets.
		/// </param>
		public async Task DoTeleport(uint characterId, uint mapId, Vector3? toPosition = null, bool epic = false)
		{
			var client = _connections.ForCharacter(characterId);
			if (client?.Session.Character == null)
				return;

			var map = await _world.GetMap(mapId);
			if (map == null)
			{
				await _chat.SendSystemMessage(characterId, ChatMarkup.Color("red", $"Unknown map id: {mapId}"));
				return;
			}

			// The destination is either an explicit point - a bind, a recall, or typed coordinates - or the map's
			// default spawn: the always-known starter, else any respawn point. A map with no points defined cannot
			// resolve a spawn, so the teleport is refused rather than dumping the player at the origin. It is persisted
			// before the handoff, so the reconnect's world entry, which loads Character.MapId, lands at the right spot.
			Vector3 position;
			Quaternion rotation;
			if (toPosition.HasValue)
			{
				position = toPosition.Value;
				rotation = Quaternion.Identity;
			}
			else
			{
				var spawn = await _world.GetDefaultSpawn(map.Id);
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
			await _characters.SaveCharacterTransform(client.Session.Character.Id, position, rotation, map.Id);

			var mapLabel = string.IsNullOrEmpty(map.Name)
				? map.Id.ToString()
				: $"{map.Name} ({map.Id})";
			await _chat.SendSystemMessage(characterId, $"Teleporting to {mapLabel}...");

			// The retail changemap sequence, decoded consistently from both changemap captures: on the live connection,
			// announce the destination - 0x10D27BC0 "begin teleport to map N", then 0x5A32F0D7 with the destination map
			// and spawn position - and then the handoff advert, after which the client reconnects and does a fresh
			// world entry on the new map. All three ride the handoff conversation, 0xb171ce13 to 0x6c67c147.
			//
			// Marking the client as a teleport re-entry is what makes the reconnect Hello arm the post-0x205F
			// follow-up; the changemap reconnect carries no 0x205F of its own, so ReadyForPlayScreen would never fire
			// and the loading screen would hang. It is consumed in the Hello existing-character branch.
			_pendingTeleportReentry.TryAdd(client.Session.Character.Id, 0);
			if (epic)
				_pendingEpicEntry.TryAdd(client.Session.Character.Id, 0);
			else
				_pendingEpicEntry.TryRemove(client.Session.Character.Id, out _);

			// The destination position has just been persisted, so this old connection's imminent disconnect must not
			// save its stale pre-teleport position back over it: the disconnect path fires a SavePlayerState that would
			// otherwise clobber the destination.
			client.Session.IsTeleportingOut = true;

			await SendChangeMapPrep(client, map.Id, position, epic: epic);
			await SendWorldHandoff(client);
		}

		/// <summary>
		///     The changemap prep bodies - the begin and the destination announce - that the retail teleport sends
		///     before the handoff advert.
		/// </summary>
		public async Task SendChangeMapPrep(INetworkClient client, uint destMapId, Vector3 position, uint destDescriptor = 0x00000014u, bool epic = false)
		{
			var instance = client.Session.Character.Id;
			// Source the destination playfield token from the destination map's burst so the announce matches the token
			// the reconnect's world entry stamps: it is per playfield instance, not a constant.
			var destToken = await _broadcaster.ResolveMapPlayfieldToken(destMapId);
			// The zero-length 0x1B goes first: retail precedes every IntendedTeleport with it, 102 of 102 across the
			// corpus. The client discards it - its handler is an empty override - and it is sent because retail does.
			await GameWire.SendTeleportPending(client);
			foreach (var body in new[] { TeleportCodec.Build(instance, destMapId, epic), TeleportCodec.Build(instance, destMapId, position, destToken, destDescriptor, epic) })
			{
				await client.SendAsync(new Rpc(CreateFlowWire.MorphAckSender, CreateFlowWire.Receiver(client), GameInterfaceMethods.DispatchPassBlob, s => s.WriteArrayUInt32Length(body)));
			}
		}

		public async Task SendWorldHandoff(INetworkClient client)
		{
			var realm = await _realms.GetRealm(client.Session.Character.RealmId);
			var address = $"{realm!.IpAddress}:{realm.Port}";

			await client.SendAsync(new Rpc(CreateFlowWire.MorphAckSender, CreateFlowWire.Receiver(client), GameInterfaceMethods.DispatchRedirectToServer,
				b => b.WriteByte(0x00).WriteStringByteLength(address)));

			_logger.LogInformation("Sent enter-world handoff (0x2002) advertising world server {Address}.", address);
		}

		// The handoff latches, armed in DoTeleport above and consumed by the listener's reconnect path.

		/// <summary>
		///     Whether a teleport re-entry is armed for this client instance. A peek: the login branch consumes it.
		/// </summary>
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

		/// <summary>
		///     Hold a character's live buffs over a zone change, so the entity the re-entry builds inherits them.
		///     Called at the world connection's tear-down, and only when that tear-down is a teleport out.
		/// </summary>
		public void CarryBuffs(uint characterId, ICollection<KeyValuePair<uint, ActiveBuff>> buffs)
		{
			_carriedBuffs.Carry(characterId, buffs, DateTimeOffset.UtcNow);
		}

		/// <summary>The buffs held over a zone change, removed from the hold. Empty when there are none.</summary>
		public IReadOnlyList<KeyValuePair<uint, ActiveBuff>> ConsumeCarriedBuffs(uint characterId)
		{
			return _carriedBuffs.Consume(characterId);
		}

		/// <summary>Recall to the bound location, the persisted bind point.</summary>
		/// <remarks>
		///     The retail triggers that land here are the Path of Asura spell and a bindstone recall; the GM command is
		///     the test bootstrap. This is not the Totem of Origins, which returns to the racial home city through
		///     <see cref="RecallToRacialHomeCommand" />. No on-screen line precedes the handoff, because retail sends
		///     none.
		/// </remarks>
		public async Task RecallToBindCommand(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character.BindRespawnPointId is not { } pointId)
			{
				await _chat.SendSystemMessage(character.Id, "You have no bind point. Use .bind to set one.");
				return;
			}

			var point = await _world.GetRespawnPoint(pointId);
			if (point == null)
			{
				await _chat.SendSystemMessage(character.Id, ChatMarkup.Color("red", "Your bind point no longer exists. Re-bind with .bind."));
				return;
			}

			await this.DoTeleport(character.Id, point.MapId, point.Position);
		}

		/// <summary>
		///     Use the Totem of Origins: return to the character's racial home city, which is not the bind point. See
		///     <c>StartingData.RacialHomeMap</c> for the mapping and the capture that pins the Stygian row.
		/// </summary>
		/// <remarks>
		///     Bound location and racial origin are two separate destinations, and the Totem ignores the bind entirely,
		///     so this shares no state with <see cref="RecallToBindCommand" /> beyond the handoff. No on-screen line
		///     precedes the handoff, as with the bind recall.
		/// </remarks>
		public async Task RecallToRacialHomeCommand(INetworkClient client)
		{
			var character = client.Session.Character;
			if (!StartingData.RacialHomeMap.TryGetValue(character.Race, out var homeMapId))
			{
				// Logged rather than silent: an unmapped race is a content gap in RacialHomeMap, not a corrupt character.
				_logger.LogWarning("Totem of Origins: race {Race} has no home city mapped, so {CharacterId:X8} cannot recall home.", character.Race, character.Id);
				await _chat.SendSystemMessage(character.Id, ChatMarkup.Color("red", $"No home city is mapped for race {character.Race}."));
				return;
			}

			var point = await _world.GetDefaultSpawn(homeMapId);
			if (point == null)
			{
				_logger.LogWarning("Totem of Origins: home map {Map} ({Race}) has no respawn point.", homeMapId, character.Race);
				await _chat.SendSystemMessage(character.Id, ChatMarkup.Color("red", $"Home map {homeMapId} has no respawn point."));
				return;
			}

			await this.DoTeleport(character.Id, point.MapId, point.Position);
		}
	}

	/// <summary>
	///     Live buffs held across a zone-change handoff, keyed by character id.
	/// </summary>
	/// <remarks>
	///     A zone change destroys the world connection, and <c>PlayerRegistry</c> is keyed by connection, so the
	///     re-entry builds a new <see cref="WorldEntity" /> and everything the old one held that is not persisted is
	///     dropped.
	///     <para />
	///     That is not cosmetic for a debuff: a live NPC-applied debuff whose entry dies with the entity has nothing
	///     left to expire it, so the buff tick never sends its removal twin, and the client - which keeps its own icon
	///     list across a map change - shows it at 0 s for ever.
	///     <para />
	///     Retail carries live effects across the change rather than ending them at the door. In
	///     <c>xxx_mage-necromancer_login_play3</c>, <c>RequestACGTeleport</c> at frame 4732 is followed at 4749 by five
	///     apply-form <c>MagicBuffIIR</c> records re-stating the character's live buffs on the far side, so carrying is
	///     the retail-shaped behaviour.
	///     <para />
	///     Keyed by character id for the same reason <c>PlayerDeathService</c>'s self-resurrection offer is: the state
	///     has to outlive the connection that owned it. It is not persisted, so a server restart drops it.
	/// </remarks>
	public sealed class CarriedBuffHold
	{
		private readonly ConcurrentDictionary<uint, IReadOnlyList<KeyValuePair<uint, ActiveBuff>>> _held =
			new ConcurrentDictionary<uint, IReadOnlyList<KeyValuePair<uint, ActiveBuff>>>();

		/// <summary>
		///     Hold this character's still-live buffs, replacing anything held before.
		/// </summary>
		/// <remarks>
		///     An already-expired buff is dropped rather than carried: the tick removes an expired buff and sends its
		///     twin, so one still in the map at tear-down is one the tick has not reached yet, and carrying a dead one
		///     would re-arm an icon the client has already been told about.
		/// </remarks>
		public void Carry(uint characterId, ICollection<KeyValuePair<uint, ActiveBuff>> buffs, DateTimeOffset now)
		{
			var live = buffs.Where(b => !b.Value.Expired(now)).ToArray();
			if (live.Length == 0)
			{
				_held.TryRemove(characterId, out _);

				return;
			}

			_held[characterId] = live;
		}

		/// <summary>
		///     What is held for this character, removed from the hold so a second entry cannot re-arm the buffs.
		/// </summary>
		public IReadOnlyList<KeyValuePair<uint, ActiveBuff>> Consume(uint characterId)
		{
			return _held.TryRemove(characterId, out var buffs) ? buffs : [];
		}


	}
}
