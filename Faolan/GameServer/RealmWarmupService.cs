using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Faolan.GameServer.Combat;
using Faolan.GameServer.Quest;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	// Builds the once-per-server catalogs at startup instead of leaving them to the first player who triggers each
	// subsystem. Every one of them is a lazily built singleton behind its own load lock: correct as a lazy load, but it
	// means whoever first casts, is first hit, or first walks up to a quest giver pays the whole build inside their
	// world entry - the "first player in the map" spike.
	//
	// These costs are once per process and shared by every map, so warming them here takes work that happens anyway off
	// the critical path of a live entry. Measured on this checkout's data: BuffStatEffects ~400 ms, the combo catalog
	// ~35 ms, the quest-giver and goal-target maps ~15 ms, plus the pool and triggered-effect tables.
	//
	// It deliberately does not warm per-map state (a zone's NPCs, its terrain). That is a different lifetime - once per
	// map rather than once per server - and eagerly loading every map's zone at startup would build worlds no one has
	// entered and take instance ownership for a player who may never arrive. The entry path warms the terrain of the
	// map being entered off the tick; the zone NPC load stays lazy.
	//
	// Fire-and-forget on ApplicationStarted, exactly as RealmStatusService flips Online: StartAsync is too early, with
	// the container still being built, and blocking it would delay the listeners binding. A warm that throws is logged
	// and swallowed, because the lazy load behind each still runs on first use, so a failed warm degrades to the lazy
	// behaviour rather than breaking the server.
	public sealed class RealmWarmupService : IHostedService
	{
		private readonly BuffStatEffects _buffs;
		private readonly SpellPoolEffects _pools;
		private readonly SpellEffectService _effects;
		private readonly ComboService _combos;
		private readonly QuestMarkerResolver _questMarkers;
		private readonly ILogger<RealmWarmupService> _logger;
		private readonly IHostApplicationLifetime _lifetime;

		public RealmWarmupService(BuffStatEffects buffs, SpellPoolEffects pools, SpellEffectService effects, ComboService combos,
			QuestMarkerResolver questMarkers, ILogger<RealmWarmupService> logger, IHostApplicationLifetime lifetime)
		{
			_buffs = buffs;
			_pools = pools;
			_effects = effects;
			_combos = combos;
			_questMarkers = questMarkers;
			_logger = logger;
			_lifetime = lifetime;
		}

		public Task StartAsync(CancellationToken cancellationToken)
		{
			_lifetime.ApplicationStarted.Register(() => _ = WarmAsync());
			return Task.CompletedTask;
		}

		public Task StopAsync(CancellationToken cancellationToken)
		{
			return Task.CompletedTask;
		}

		private async Task WarmAsync()
		{
			try
			{
				var clock = Stopwatch.StartNew();
				// Sequential on purpose: they share one pooled DbContext factory, and the point is to move cost off a
				// live entry, not to race the startup. Each logs its own build time.
				await _buffs.WarmAsync();
				await _pools.WarmAsync();
				await _effects.WarmAsync();
				await _combos.WarmAsync();
				await _questMarkers.WarmAsync();
				_logger.LogInformation("Realm warmup: once-per-server catalogs built in {Ms} ms - no player pays their first-touch (#938).", clock.ElapsedMilliseconds);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Realm warmup failed; catalogs will build lazily on first use instead (#938).");
			}
		}
	}
}
