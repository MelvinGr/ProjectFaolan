using System;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Enums;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Faolan
{
	// #324: keeps the hosting realm's Realm.OnlineStatus in sync with the process lifecycle - so the login tier's realm
	// list (SendRealmList) shows which realms are actually up. Registered only in the realm role; each process owns
	// just its own realm row (updated by id, so realm hosts on a shared DB never touch each other's).
	//
	// ⭐ **Online means "this host is SERVING", not "this host began starting"** (#909). It used to be flipped in
	// `StartAsync`, which is the wrong moment twice over: hosted services start before the host is known to be up, and
	// anything that fails after that point leaves a lie in the table. It did exactly that - a realm process logged
	// "Realm 2 marked Online", then died on a Kestrel bind (#910) with its ports never listening, and the login tier
	// would have advertised realm 2 and handed clients to a host that did not exist. That is the failure mode #324 is
	// most exposed to and the hardest to notice, because it fails quietly in the direction of pointing players at a
	// dead realm.
	//
	// So the flip hangs off `IHostApplicationLifetime.ApplicationStarted`, which fires only once every hosted service
	// has started AND the web host (when there is one) has bound. A startup failure now leaves the row Offline.
	//
	// ⭐ And startup CLAIMS the row: `StartAsync` writes Offline before anything else. That is what clears a stale
	// Online left by this realm's own previous life - a crash, a `kill -9`, an unhandled exception or a bind failure
	// all skip `StopAsync`, so without this the row stays Online indefinitely, including across a reboot.
	//
	// ⚠️ The claim is safe because owning the row means owning the PORTS. The listeners create their sockets in their
	// constructors (see Program.cs), and hosted services are constructed before the first `StartAsync` runs - so by
	// the time this executes, this process has already bound the realm's world/chat/cs ports and no other process can
	// be serving that realm. Any Online it finds is therefore stale by construction, not a rival host.
	//
	// ⭐ And it HEARTBEATS while it serves (#922). The flag alone cannot express "this host died and nobody restarted
	// it": the claim above only fixes a realm's OWN restart, so a crash with no restart left the row Online for ever
	// and the login tier kept handing clients to a host that was not there. A two-value column cannot decay on its
	// own - somebody has to observe the absence - so this stamps `Realm.LastSeenUtc` every
	// `RealmLiveness.HeartbeatInterval` and a reader treats Online-but-stale as Offline (`RealmLiveness.IsServing`).
	//
	// ⚠️ A stamp rather than a lease or a reaper, deliberately, because it keeps the invariant above UNCHANGED: this
	// process writes only its own row, and no process writes another's. The login tier only reads.
	public sealed class RealmStatusService : IHostedService
	{
		private readonly IRealmRepository _database;
		private readonly ILogger<RealmStatusService> _logger;
		private readonly uint _realmId;
		private readonly IHostApplicationLifetime _lifetime;

		// The heartbeat loop, started when the host begins SERVING and stopped on shutdown. Its own CTS rather than
		// StopAsync's token: the token passed to StopAsync is a shutdown TIMEOUT, and cancelling on it would leave the
		// loop running past a slow shutdown.
		private readonly CancellationTokenSource _stopping = new CancellationTokenSource();
		private Task? _heartbeat;

		public RealmStatusService(IRealmRepository database, ILogger<RealmStatusService> logger, HostRealm realm, IHostApplicationLifetime lifetime)
		{
			_database = database;
			_logger = logger;
			_realmId = realm.Id;
			_lifetime = lifetime;
		}

		public async Task StartAsync(CancellationToken cancellationToken)
		{
			// CLAIM: whatever the row said, this process now owns the realm's ports, so an Online in it is stale.
			if (await _database.SetRealmOnlineStatus(_realmId, RealmOnlineStatus.Offline))
				_logger.LogInformation("Realm {RealmId} claimed - held Offline until the host is serving.", _realmId);

			// The flip to Online happens only once the whole host is up. Registering the callback is all StartAsync
			// does, because StartAsync itself is too early to mean anything (see the remark above).
			_lifetime.ApplicationStarted.Register(() => _ = MarkOnline());
		}

		public async Task StopAsync(CancellationToken cancellationToken)
		{
			await _stopping.CancelAsync();
			if (_heartbeat != null)
				await _heartbeat; // it swallows its own cancellation, so this cannot throw the shutdown down

			if (await _database.SetRealmOnlineStatus(_realmId, RealmOnlineStatus.Offline))
				_logger.LogInformation("Realm {RealmId} marked Offline.", _realmId);
		}

		/// <summary>
		///     Stamps <c>Realm.LastSeenUtc</c> while this host serves, so a reader can tell a live realm from one that
		///     crashed and was never restarted (#922).
		/// </summary>
		/// <remarks>
		///     ⚠️ A failed stamp is logged and the loop CONTINUES. A database blip must not stop the heartbeat: the
		///     realm is still serving, and the consequence of giving up would be to take a healthy realm out of the
		///     list at the next TTL - failing in the direction this ticket exists to avoid, just the other way round.
		///     The TTL's own margin (four intervals) is what absorbs a blip that lasts.
		/// </remarks>
		private async Task Heartbeat()
		{
			using var timer = new PeriodicTimer(RealmLiveness.HeartbeatInterval);
			try
			{
				while (await timer.WaitForNextTickAsync(_stopping.Token))
				{
					try
					{
						await _database.TouchRealm(_realmId);
					}
					catch (System.Exception ex)
					{
						_logger.LogWarning(ex, "Realm {RealmId} heartbeat failed; it reads as down if this persists past {Stale}.", _realmId, RealmLiveness.StaleAfter);
					}
				}
			}
			catch (OperationCanceledException)
			{
				// shutting down
			}
		}

		private async Task MarkOnline()
		{
			try
			{
				if (await _database.SetRealmOnlineStatus(_realmId, RealmOnlineStatus.Online))
					_logger.LogInformation("Realm {RealmId} marked Online.", _realmId);

				// Started only once the flip SUCCEEDED - a realm that could not mark itself Online is not serving as
				// far as any reader is concerned, and heartbeating a row that says Offline would say nothing.
				_heartbeat = Heartbeat();
			}
			catch (System.Exception ex)
			{
				// A lifetime callback runs outside the startup path, so a throw here would go nowhere. Leaving the row
				// Offline is the safe direction - the realm simply does not advertise - but it must not be silent.
				_logger.LogError(ex, "Realm {RealmId} is serving but could not be marked Online; it will not appear in the realm list.", _realmId);
			}
		}
	}
}
