using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	// Player-state persistence, pulled out of the GameServerListener/GMCommands god-class (#200): the periodic
	// autosave loop plus the one-off flushes (logout, teleport, the .save GM command). Each flush writes a
	// connected player's authoritative live WorldEntity transform (position/rotation/map) onto their Character row -
	// movement is otherwise never persisted, so without this a logout loses where the player was. The player
	// registry is owned by WorldSimulation; this service only reads it and writes to the database, so its dependency
	// surface is the narrow (Database, Logger, WorldSimulation) - it never reaches back into the listener.
	public sealed class PlayerPersistenceService
	{
		private static readonly TimeSpan AutoSaveInterval = TimeSpan.FromMinutes(5);

		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;
		private readonly WorldSimulation _simulation;
		private Task _autoSaveTask = Task.CompletedTask;

		public PlayerPersistenceService(IDatabaseRepository database, ILogger logger, WorldSimulation simulation)
		{
			_database = database;
			_logger = logger;
			_simulation = simulation;
		}

		// Flushes every connected player's live world state; returns how many were saved. Used by the autosave loop
		// and the .save GM command.
		public async Task<int> FlushAllAsync()
		{
			var saved = 0;
			foreach (var pair in _simulation.Players)
			{
				if (await SavePlayerState(pair.Key, pair.Value))
					saved++;
			}

			return saved;
		}

		// Flushes one player's authoritative live transform (the WorldEntity) onto their persisted Character and
		// saves it. Used by the autosave loop, .save, and disconnect. Errors are logged (callers may not await).
		public async Task<bool> SavePlayerState(INetworkClient client, WorldEntity entity)
		{
			var character = client.Session?.Character;
			if (character == null)
				return false;

			try
			{
				// entity is the live WorldEntity (Vector3 Euler rotation); the DB Character stores a quaternion.
				character.MapId = entity.MapId;
				character.Position = entity.Position;
				character.Rotation = entity.Rotation;
				await _database.SaveCharacterTransform(character.Id, entity.Position, entity.Rotation, entity.MapId);

				// Snapshot the live self-buffs with their absolute expiry so a relog restores them with the time left
				// (#274). Only the buffs no dedicated re-applier owns (shield/mount are excluded) and that haven't
				// already expired; RestoreBuffs re-sends them at world entry. Replace-all, like the transform above -
				// the row set always mirrors what's currently active.
				var now = DateTimeOffset.UtcNow;
				var buffs = entity.ActiveBuffs.Values
					.Where(b => GameServerListener.ShouldPersistBuff(entity, b) && !b.Expired(now))
					.Select(b => new CharacterBuff
					{
						BuffId = b.BuffId,
						Nonce = b.Nonce,
						IsLongForm = b.IsLongForm,
						ExpiresAt = b.ExpiresAt
					})
					.ToList();
				await _database.SaveCharacterBuffs(character.Id, buffs);
				return true;
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to flush world state for {Name} (0x{Handle:X8}).", entity.Name, entity.Handle);
				return false;
			}
		}

		// Starts the periodic crash-safety flush of the live world state. Mirrors WorldSimulation.Start/StopAsync:
		// the listener owns the lifetime token and awaits StopAsync() on shutdown so a mid-tick save isn't raced.
		public void Start(CancellationToken cancellationToken)
		{
			_autoSaveTask = AutoSaveLoop(cancellationToken);
		}

		public Task StopAsync()
		{
			return _autoSaveTask;
		}

		private async Task AutoSaveLoop(CancellationToken cancellationToken)
		{
			using var timer = new PeriodicTimer(AutoSaveInterval);
			try
			{
				while (await timer.WaitForNextTickAsync(cancellationToken))
				{
					var saved = await FlushAllAsync();
					if (saved > 0)
						_logger.LogInformation("Autosave: flushed {Count} player(s)' live world state.", saved);
				}
			}
			catch (OperationCanceledException)
			{
				// server shutting down
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Autosave loop failed.");
			}
		}
	}
}
