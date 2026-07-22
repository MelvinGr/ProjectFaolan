using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database
{
	// Brings a database up to a runnable state: migrate to the latest schema for the active provider, then ensure the
	// default realm + account exist. Shared by the server (DatabaseMigrator) and the importer so a fresh DB is brought
	// up the SAME way on either path - the one place the migrate+seed sequence lives. Returns what was seeded so the
	// caller logs in its own style (ILogger on the server, Console in the importer).
	public static class DatabaseInitializer
	{
		public static async Task<InitResult> InitializeAsync(DatabaseContext db, CancellationToken cancellationToken = default)
		{
			// Both providers migrate the same way - each has its own migration project (see UseFaolanDatabase);
			// Migrate applies the pending set for the active provider.
			await db.Database.MigrateAsync(cancellationToken);

			// A world can't serve without a realm (guild/AH/mail/character FK it, #324) or an account to log into.
			// These are config state, not schema, so they're ensured here rather than shipped in a migration. Idempotent.
			var realmCreated = await DefaultSeeder.EnsureDefaultRealmAsync(db, cancellationToken);
			var accountCreated = await DefaultSeeder.EnsureDefaultAccountAsync(db, cancellationToken);
			return new InitResult(realmCreated, accountCreated);
		}

		public readonly record struct InitResult(bool RealmCreated, bool AccountCreated);
	}
}
