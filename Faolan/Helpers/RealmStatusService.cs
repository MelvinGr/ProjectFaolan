using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Enums;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Faolan.Helpers
{
	// #324: keeps the hosting realm's Realm.OnlineStatus in sync with the process lifecycle - Online when the realm
	// game host starts, Offline on graceful shutdown - so the login tier's realm list (SendRealmList) shows which
	// realms are actually up. Registered only in the realm role; each process owns just its own realm row (updated by
	// id, so realm hosts on a shared DB never touch each other's).
	//
	// NOTE: a HARD crash / SIGKILL skips StopAsync, so the row can be left stale-Online; graceful stop (Ctrl-C, or
	// `docker stop` -> SIGTERM) flips it to Offline. A heartbeat/last-seen TTL is the robust follow-up if stale-Online
	// after a crash becomes a problem (see the multi-realm plan).
	public sealed class RealmStatusService : IHostedService
	{
		private readonly IRealmRepository _database;
		private readonly ILogger<RealmStatusService> _logger;
		private readonly uint _realmId;

		public RealmStatusService(IRealmRepository database, ILogger<RealmStatusService> logger, uint realmId)
		{
			_database = database;
			_logger = logger;
			_realmId = realmId;
		}

		public async Task StartAsync(CancellationToken cancellationToken)
		{
			if (await _database.SetRealmOnlineStatus(_realmId, RealmOnlineStatus.Online))
				_logger.LogInformation("Realm {RealmId} marked Online.", _realmId);
		}

		public async Task StopAsync(CancellationToken cancellationToken)
		{
			if (await _database.SetRealmOnlineStatus(_realmId, RealmOnlineStatus.Offline))
				_logger.LogInformation("Realm {RealmId} marked Offline.", _realmId);
		}
	}
}
