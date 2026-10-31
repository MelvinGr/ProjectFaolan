using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.GameServer.Combat;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     Player-state persistence: the periodic autosave loop, plus the one-off flushes on logout, teleport and the
	///     <c>.save</c> GM command.
	/// </summary>
	/// <remarks>
	///     Each flush writes a connected player's authoritative live <see cref="WorldEntity" /> transform - position,
	///     rotation and map - onto their Character row. Movement is not otherwise persisted, so without this a logout
	///     loses where the player was. The player registry is owned by <see cref="WorldSimulation" />; this service
	///     only reads it and writes to the database.
	/// </remarks>
	public sealed class PlayerPersistenceService
	{
		private static readonly TimeSpan AutoSaveInterval = TimeSpan.FromMinutes(5);

		private readonly ICharacterRepository _characters;
		private readonly ICharacterStatRepository _stats;
		private readonly ILogger _logger;
		private readonly WorldSimulation _simulation;
		private Task _autoSaveTask = Task.CompletedTask;

		public PlayerPersistenceService(ICharacterRepository characters, ICharacterStatRepository stats, ILogger logger, WorldSimulation simulation)
		{
			_characters = characters;
			_stats = stats;
			_logger = logger;
			_simulation = simulation;
		}

		/// <summary>
		///     Flushes every connected player's live world state, for the autosave loop, the shutdown flush and the
		///     <c>.save</c> GM command. Each zone's players are saved on that zone's loop, since the save reads their
		///     live state.
		/// </summary>
		/// <returns>
		///     How many were saved. From inside a zone's own work (<c>.save</c>) the other zones' saves are queued rather
		///     than awaited - a zone never waits on another loop - so the count covers that zone alone.
		/// </returns>
		public async Task<int> FlushAllAsync()
		{
			var saved = 0;
			foreach (var zone in _simulation.Players.Values.Select(p => _simulation.World.ZoneForEntity(p)).Distinct().ToList())
			{
				await _simulation.RunInZone(zone, async () =>
				{
					foreach (var pair in _simulation.Players.In(zone).ToList())
					{
						if (await SavePlayerState(pair.Key, pair.Value))
							Interlocked.Increment(ref saved);
					}
				});
			}

			return saved;
		}

		/// <summary>
		///     Flushes one player's authoritative live transform onto their persisted Character and saves it, for the
		///     autosave loop, <c>.save</c> and disconnect. Errors are logged, because callers may not await.
		/// </summary>
		public async Task<bool> SavePlayerState(INetworkClient client, WorldEntity entity)
		{
			var character = client.Session.Character;

			try
			{
				// The entity holds the live transform; the persisted Character row stores the same values.
				character.MapId = entity.MapId;
				character.Position = entity.Position;
				character.Rotation = entity.Rotation;
				await _characters.SaveCharacterTransform(character.Id, entity.Position, entity.Rotation, entity.MapId);

				// Snapshot the live self-buffs with their absolute expiry, so a relog restores them with the time left.
				// Only the buffs no dedicated re-applier owns - shield and mount are excluded - and that have not
				// already expired; world entry re-sends them. Replace-all, like the transform above, so the row set
				// always mirrors what is currently active.
				var now = DateTimeOffset.UtcNow;
				var buffs = entity
					.ActiveBuffs.Values.Where(b => BuffService.ShouldPersistBuff(entity, b) && !b.Expired(now))
					.Select(b => new CharacterBuff
					{
						BuffId = b.BuffId,
						ExpiresAt = b.ExpiresAt
					})
					.ToList();
				await _stats.SaveCharacterBuffs(character.Id, buffs);
				return true;
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to flush world state for {Name} (0x{Handle:X8}).", entity.Name, entity.Handle);
				return false;
			}
		}

		/// <summary>
		///     Starts the periodic crash-safety flush of the live world state.
		/// </summary>
		/// <remarks>
		///     Mirrors <see cref="WorldSimulation" />'s start and stop: the listener owns the lifetime token and awaits
		///     <see cref="StopAsync" /> on shutdown, so a mid-tick save is not raced.
		/// </remarks>
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
