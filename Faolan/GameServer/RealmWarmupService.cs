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
	// Builds the once-per-SERVER catalogs at startup instead of leaving them to the first player who triggers each
	// subsystem (Gitea #938). Every one of these is a lazily built singleton behind its own load lock: correct as a
	// lazy load, but it means whoever first casts, is first hit, or first walks up to a quest giver pays the whole
	// build inside their world entry - the "first player in the map" spike.
	//
	// ⭐ These costs are once per PROCESS and shared by every map, so warming them here is a pure win with no
	// downside: the work happens anyway, and doing it at startup takes it off the critical path of a live entry.
	// Measured on this checkout's data (`--report`-style entry profile): BuffStatEffects ~400 ms, the combo catalog
	// ~35 ms, the quest-giver/goal-target maps ~15 ms, plus the pool- and triggered-effect tables.
	//
	// ⚠️ It does NOT warm per-MAP state (a zone's NPCs, its terrain). That is a different lifetime - once per map,
	// not once per server - and eagerly loading every map's zone at startup would build worlds no one has entered
	// and take instance ownership (#268) for a player who may never arrive. The entry path already warms the
	// terrain of the map being entered off the tick; the zone NPC load stays lazy. See #938.
	//
	// ⚠️ Fire-and-forget on ApplicationStarted, exactly as RealmStatusService flips Online: StartAsync is too early
	// (the container is still being built) and blocking it would delay the listeners binding. A warm that throws is
	// logged and swallowed - the lazy load behind each still runs on first use, so a failed warm degrades to the old
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
