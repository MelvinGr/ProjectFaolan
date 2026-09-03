using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Constants;
using Faolan.GameServer.Combat;
using Microsoft.Extensions.Logging;
using Faolan.GameServer.Wire;

namespace Faolan.GameServer.World
{
	// Getting ON and OFF a mount (#26 / #142 / #428). Three paths reach it - the summon/dismount channel the client
	// asks for, the re-seat on world entry into a mount-allowed map, and the involuntary dismount the world tick
	// forces (a hit that throws the rider, a death, a no-mount area) - and all three send the same two things: the
	// Ride buff and the `Mount` / `MountSpeed` stat pair.
	//
	// Seating used to be written out twice (world entry and the summon channel) with the viewer broadcast on only one
	// of them, which is the shape #428 was: a horse that rendered for its rider and for nobody else.
	public sealed class MountService
	{
		private readonly WorldBroadcaster _broadcaster;
		private readonly BuffService _buffs;
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;
		private readonly WorldSimulation _sim;

		public MountService(WorldSimulation sim, IDatabaseRepository database, BuffService buffs, WorldBroadcaster broadcaster, ILogger logger)
		{
			_sim = sim;
			_database = database;
			_buffs = buffs;
			_broadcaster = broadcaster;
			_logger = logger;
		}

		/// <summary>
		///     Seats the player on <paramref name="mount" />: the Ride buff, the stat pair, and the live entity's mount
		///     state. <paramref name="broadcastToViewers" /> pushes the same stats to onlookers - the horse renders from
		///     THEM, so a viewer who never gets them sees an unmounted player running at mount speed (#428).
		/// </summary>
		public async Task Seat(INetworkClient client, Mount mount, bool broadcastToViewers)
		{
			var self = client.Session.Character.Id;
			await _buffs.SendMagicBuffIir(client, self, self, MountCatalog.RideSpell, MountCatalog.RideBuffPayload(BuffService.CasterLevel(client)));

			var stats = new CharacterStatBlock
			{
				[CharacterStatId.Mount] = mount.CreatureId,
				[CharacterStatId.MountSpeed] = MountCatalog.MountSpeed
			};
			await GameWire.SendStatUpdate(client, stats, self);
			if (broadcastToViewers)
				await _broadcaster.BroadcastStatsToViewers(client, stats);

			var entity = _sim.PlayerWorldEntity(client);
			entity?.IsMounted = true;
			entity?.MountRideStat = mount.CreatureId; // so a viewer arriving LATER can be told (#428)
			entity?.MountDamageTaken = 0; // a fresh mount soaks its own beating before it throws you (#142)
		}

		/// <summary>Seats the player and PERSISTS the mount, so it survives a relog or a trip through a no-mount area.</summary>
		public async Task SeatAndPersist(INetworkClient client, Mount mount)
		{
			await Seat(client, mount, true);
			client.Session.Character.MountId = mount.Id;
			await _database.SaveCharacterMount(client.Session.Character.Id, mount.Id);
		}

		// Takes the player off their mount and clears the persisted one, so world entry does not re-seat a mount they
		// no longer have (#142). The voluntary dismount key press (after DismountChannelMs), a hit that throws the
		// rider, and the respawn after a death all reach it. No-op when they are not mounted.
		public async Task Dismount(INetworkClient client)
		{
			var character = client.Session.Character;
			var entity = _sim.PlayerWorldEntity(client);
			if (entity?.IsMounted != true && character?.MountId == null)
				return;

			var self = character.Id;
			await _buffs.ApplySpell(client, self, self, MountCatalog.DismountSpell);
			var stats = new CharacterStatBlock
			{
				[CharacterStatId.Mount] = 0
			};
			await GameWire.SendStatUpdate(client, stats, self);
			await _broadcaster.BroadcastStatsToViewers(client, stats);

			entity?.IsMounted = false;
			entity?.MountRideStat = 0;
			entity?.MountDamageTaken = 0;

			character.MountId = null;
			await _database.SaveCharacterMount(self, null);
		}

		/// <summary>
		///     Re-seats the character's persisted mount on world entry, if the destination map allows mounts. Silent
		///     when they have none, when the map forbids it, or when the row is missing.
		/// </summary>
		public async Task<bool> RestoreOnWorldEntry(INetworkClient client, uint mountId, Map? map, uint mountAllowedFlag)
		{
			if (((map?.PlayfieldFlags ?? 0) & mountAllowedFlag) == 0)
				return false;

			var mount = await _database.GetMount(mountId);
			if (mount == null)
				return false;

			// Entry-time seating does NOT broadcast: the player is the only one in the burst, and every viewer who
			// later streams them in is told from WorldEntity.MountRideStat, which Seat stamps.
			await Seat(client, mount, false);
			_logger.LogInformation("Restored mount {Mount} for {Name} on map {Map}.", mount.Spell.Name, client.Session.Character.Name, client.Session.Character.MapId);
			return true;
		}
	}
}
