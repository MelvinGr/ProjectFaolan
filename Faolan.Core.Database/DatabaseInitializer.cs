using System;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database
{
	// Brings a database up to a runnable state: migrate to the latest schema for the active provider, load the game
	// data if the database has none, then ensure the default realm + account exist. Shared by the server and the
	// importer so a fresh DB is brought up the SAME way on either path - the one place that sequence lives. Returns
	// what happened so the caller logs in its own style (ILogger on the server, Console in the importer).
	public static class DatabaseInitializer
	{
		// The default admin login. The password is a REAL hash of PasswordHashing.DefaultSeedPassword ("faolan"):
		// it used to be an empty string, which PasswordHashing.Matches treated as "no password set, accept
		// anything" - that sentinel is gone, so an empty value would now authenticate nothing and a fresh clone
		// could not log in. Administrator + Active mirrors the committed reference account.
		public static async Task<bool> EnsureDefaultAccountAsync(DatabaseContext db, CancellationToken cancellationToken = default)
		{
			if (await db.Accounts.AnyAsync(cancellationToken))
				return false;

			db.Accounts.Add(new Account
			{
				UserName = "faolan",
				Password = PasswordHashing.Hash(PasswordHashing.DefaultSeedPassword),
				Type = AccountType.Administrator,
				State = AccountState.Active,
				LastIpAddress = ""
			});

			return await db.SaveChangesAsync(cancellationToken) == 1;
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
				Type = RealmType.PvP,
				Country = RealmCountry.English
			});

			return await db.SaveChangesAsync(cancellationToken) == 1;
		}

		// Loads faolan.sql into a database that came up with NO GAME CONTENT (#412) - what makes a fresh clone
		// runnable without the committed faolan.db. Returns false when there was nothing to do.
		//
		// "No content" is probed on Items rather than on the migration history: a database can be migrated but empty
		// (someone deleted the binary, a container started on an empty volume, a test built a schema), and those are
		// exactly the cases that want the data. A database that already HAS content is never touched, so this cannot
		// resurrect rows someone deliberately deleted.
		public static async Task<bool> EnsureGameDataAsync(DatabaseContext db, CancellationToken cancellationToken = default)
		{
			if (await db.Items.AnyAsync(cancellationToken))
				return false;

			var seed = Global.SeedPath;
			if (!File.Exists(seed))
				throw new InvalidOperationException(
					$"The database has no game content and the seed '{seed}' is missing, so the world would come up empty. Point FAOLAN_SEED at faolan.sql, or regenerate it with `dotnet run --project Faolan.Importer -- --report sql-seed`.");

			await ExecuteScriptAsync(db, seed, cancellationToken);

			// The seed is dialect-neutral, but the counter resync after it is not - identity/AUTOINCREMENT state lives
			// in sqlite_sequence vs a Postgres sequence (see SqlSeedWriter).
			var sequences = Path.ChangeExtension(Global.SeedPath, null) + (db.Database.IsNpgsql()
				? ".seq.postgres.sql"
				: ".seq.sqlite.sql");

			if (File.Exists(sequences))
				await ExecuteScriptAsync(db, sequences, cancellationToken);

			return true;
		}

		// Runs a whole script through the raw connection rather than ExecuteSqlRaw: the file carries its own
		// BEGIN/COMMIT and many statements, and both providers execute multi-statement command text as-is.
		private static async Task ExecuteScriptAsync(DatabaseContext db, string path, CancellationToken cancellationToken)
		{
			var connection = db.Database.GetDbConnection();
			if (connection.State != ConnectionState.Open)
				await connection.OpenAsync(cancellationToken);

			await using var command = connection.CreateCommand();
			command.CommandText = await File.ReadAllTextAsync(path, cancellationToken);
			command.CommandTimeout = 0; // the seed is ~110k rows; no arbitrary deadline
			await command.ExecuteNonQueryAsync(cancellationToken);
		}

		public static async Task<(bool RealmCreated, bool AccountCreated, bool DataSeeded)> InitializeAsync(DatabaseContext db, bool seedGameData = true,
			CancellationToken cancellationToken = default)
		{
			// Both providers migrate the same way - each has its own migration project (see UseFaolanDatabase);
			// Migrate applies the pending set for the active provider.
			await db.Database.MigrateAsync(cancellationToken);

			// The game data first, then the deployment defaults. The seed is CONTENT ONLY (#412) - it carries no
			// Accounts/Realms/Characters rows - so the two Ensure calls below are what give a fresh database its
			// default realm and the `faolan` admin account to log in with.
			var dataSeeded = seedGameData && await EnsureGameDataAsync(db, cancellationToken);

			// A world can't serve without a realm (guild/AH/mail/character FK it, #324) or an account to log into.
			// These are config state, not schema, so they're ensured here rather than shipped in a migration. Idempotent.
			var realmCreated = await EnsureDefaultRealmAsync(db, cancellationToken);
			var accountCreated = await EnsureDefaultAccountAsync(db, cancellationToken);
			return (realmCreated, accountCreated, dataSeeded);
		}
	}
}
