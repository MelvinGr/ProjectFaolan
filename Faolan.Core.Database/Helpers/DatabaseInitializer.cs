using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Helpers
{
	// Brings a database up to a runnable state: migrate to the latest schema for the active provider, then ensure the
	// default realm + account exist. Shared by the server (DatabaseMigrator) and the importer so a fresh DB is brought
	// up the SAME way on either path - the one place the migrate+seed sequence lives. Returns what was seeded so the
	// caller logs in its own style (ILogger on the server, Console in the importer).
	public static class DatabaseInitializer
	{
		// The default admin login (game login validation is stubbed to accept any password, see the login_encryption
		// notes, so the value is a placeholder). Administrator + Active mirrors the committed reference account.
		public static async Task<bool> EnsureDefaultAccountAsync(DatabaseContext db, CancellationToken cancellationToken = default)
		{
			if (await db.Accounts.AnyAsync(cancellationToken))
				return false;

			db.Accounts.Add(new Account
			{
				UserName = "faolan",
				Password = "faolan",
				Type = AccountType.Administrator,
				State = AccountState.Active,
				AuthChallenge = "",
				LastIpAddress = ""
			});
			await db.SaveChangesAsync(cancellationToken);
			return true;
		}

		// Ports/type mirror the committed faolan.db realm so a fresh DB matches the reference deployment. The realm's
		// id is left DB-generated (1 on a fresh DB) - realm-scoped rows resolve it by lookup, not by a hard-coded id.
		public static async Task<bool> EnsureDefaultRealmAsync(DatabaseContext db, CancellationToken cancellationToken = default)
		{
			if (await db.Realms.AnyAsync(cancellationToken))
				return false;

			db.Realms.Add(new Realm
			{
				Name = "Project Faolan",
				IpAddress = "127.0.0.1",
				Port = 7014,
				ChatServerPort = 7012,
				CsPlayerAgentPort = 7001,
				Type = RealmType.PvP
			});
			await db.SaveChangesAsync(cancellationToken);
			return true;
		}

		public static async Task<InitResult> InitializeAsync(DatabaseContext db, CancellationToken cancellationToken = default)
		{
			// Both providers migrate the same way - each has its own migration project (see UseFaolanDatabase);
			// Migrate applies the pending set for the active provider.
			await db.Database.MigrateAsync(cancellationToken);

			// A world can't serve without a realm (guild/AH/mail/character FK it, #324) or an account to log into.
			// These are config state, not schema, so they're ensured here rather than shipped in a migration. Idempotent.
			var realmCreated = await EnsureDefaultRealmAsync(db, cancellationToken);
			var accountCreated = await EnsureDefaultAccountAsync(db, cancellationToken);
			return new InitResult(realmCreated, accountCreated);
		}

		public readonly record struct InitResult(bool RealmCreated, bool AccountCreated);
	}
}
