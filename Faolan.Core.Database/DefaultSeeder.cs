using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database
{
	// Seeds the deployment's runtime/config rows that the Faolan.Importer does NOT produce: the default realm and the
	// default account. They are not schema (so they don't belong in a migration) and not reference game data (so they
	// don't belong in the importer's modules), but a world can't run without them - realm-scoped rows (AH listings,
	// guilds, mail, characters, #324) FK the realm, and a login needs an account. Postgres ENFORCES those FKs where
	// SQLite tolerates a dangling ref, so a fresh DB on any provider needs these before it is used. Both the importer
	// (seeding a fresh DB) and the server (starting on one) call these after the schema is ready. Each is idempotent -
	// a no-op once a row of its kind exists - so it never overwrites a real realm/account.
	public static class DefaultSeeder
	{
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
	}
}
