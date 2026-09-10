using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Cli;
using Faolan.Core.Crypt;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database
{
	// Brings a database up to a runnable state: migrate to the latest schema for the active provider, load the game
	// data if the database has none, then reconcile the declared realms + ensure the default account. Shared by the server and the
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
				Type = WebAccountType.Administrator,
				State = AccountState.Active,
				LastIpAddress = ""
			});

			return await db.SaveChangesAsync(cancellationToken) == 1;
		}

		// Ports/type mirror the committed faolan.db realm so a fresh DB matches the reference deployment. The realm's
		// id is left DB-generated (1 on a fresh DB) - realm-scoped rows resolve it by lookup, not by a hard-coded id.
		//
		// ⚠️ This is the UNDECLARED path only (see ReconcileRealmsAsync): it runs for a deployment that configures no
		// realms at all, and its "any realm exists, do nothing" gate is what makes that deployment's database
		// untouchable. A declared deployment goes through the reconciler instead, which addresses rows by id.
		public static async Task<bool> EnsureDefaultRealmAsync(DatabaseContext db, CancellationToken cancellationToken = default)
		{
			if (await db.Realms.AnyAsync(cancellationToken))
				return false;

			var realm = FaolanRealms.Default[0];
			db.Realms.Add(new Realm
			{
				Name = realm.Name,
				IpAddress = realm.IpAddress,
				Port = realm.Port,
				ChatServerPort = realm.ChatServerPort,
				CsPlayerAgentPort = realm.CsPlayerAgentPort,
				Type = realm.Type,
				Language = realm.Language
			});

			return await db.SaveChangesAsync(cancellationToken) == 1;
		}

		/// <summary>
		///     Brings the <c>Realms</c> table into line with what the configuration DECLARES - the static half of each
		///     row, so a realm's name, address and ports are reviewable in faolan.json instead of being edited into a
		///     committed binary database by hand.
		/// </summary>
		/// <remarks>
		///     ⚠️ It writes the STATIC columns only. The three LIVE ones are never touched here. <c>OnlineStatus</c> and
		///     <c>LastSeenUtc</c> are how a login process learns whether a realm host - possibly on another machine -
		///     is still serving (#909, #922), and the invariant that keeps that honest is that the host which owns a
		///     realm's PORTS is the only thing that writes its liveness; a file cannot observe an absence, so it does
		///     not get to claim one. <c>LoadStatus</c> is the realm's POPULATION, which nothing computes yet - and
		///     declaring it would be the reason it never could, since every restart would overwrite whatever the live
		///     count had put there. A realm this call CREATES starts Offline with a null heartbeat, which reads as
		///     down until a host says otherwise.
		///     <para />
		///     ⚠️ It never DELETES. Four tables FK a realm - characters, guilds, mail and auction listings - so a row
		///     that has dropped out of the config is reported to the caller and left standing. Removing a realm is a
		///     data migration, not a config edit.
		///     <para />
		///     <paramref name="onlyRealmId" /> scopes the write to one row, which is what a realm-role process passes:
		///     it keeps "a realm process writes only its own row" true of the static columns as well as the liveness
		///     pair. The login tier passes null and reconciles the whole declared set, because the realm list it
		///     serves is exactly that set.
		/// </remarks>
		public static async Task<RealmReconciliation> ReconcileRealmsAsync(DatabaseContext db, IReadOnlyList<DeclaredRealm>? declared,
			uint? onlyRealmId = null, CancellationToken cancellationToken = default)
		{
			// Nothing declared: the historical behaviour, unchanged. Falling through to the reconciler with the
			// built-in default instead would address the default realm by id 1 - and a database whose realm carries
			// some other id (it was DB-generated) would then gain a SECOND realm that nobody asked for.
			if (declared is not { Count: > 0 })
				return await EnsureDefaultRealmAsync(db, cancellationToken)
					? new RealmReconciliation([FaolanRealms.Default[0].Name], [], [])
					: RealmReconciliation.Nothing;

			var wanted = onlyRealmId is { } only
				? declared.Where(r => r.Id == only).ToArray()
				: [.. declared];

			var rows = await db.Realms.ToDictionaryAsync(r => r.Id, cancellationToken);
			var created = new List<string>();
			var updated = new List<string>();

			foreach (var realm in wanted)
			{
				if (!rows.TryGetValue(realm.Id, out var row))
				{
					db.Realms.Add(Row(realm));
					created.Add($"{realm.Name} (id {realm.Id})");
					continue;
				}

				// Written field by field rather than by replacing the row, so the liveness pair is not in the set of
				// things that COULD be assigned here by a later edit.
				var changes = Changes(row, realm);
				if (changes.Length == 0)
					continue;

				row.Name = realm.Name;
				row.IpAddress = realm.IpAddress;
				row.Port = realm.Port;
				row.ChatServerPort = realm.ChatServerPort;
				row.CsPlayerAgentPort = realm.CsPlayerAgentPort;
				row.Type = realm.Type;
				row.Language = realm.Language;

				updated.Add($"{realm.Name} (id {realm.Id}): {string.Join(", ", changes)}");
			}

			await db.SaveChangesAsync(cancellationToken);

			// Only meaningful for the whole-set pass: a process reconciling its own row knows nothing about whether
			// the others are declared, and saying they are not would be a guess.
			var undeclared = onlyRealmId.HasValue
				? []
				: rows.Values.Where(row => declared.All(r => r.Id != row.Id)).Select(row => $"{row.Name} (id {row.Id})").Order().ToArray();

			return new RealmReconciliation([.. created], [.. updated], undeclared);
		}

		// What the config says that the row does not, phrased as "was -> is" so the caller can LOG it. ⚠️ A port
		// change on a realm whose host is already running is the reason this is worth spelling out: the login tier
		// starts advertising the new port immediately and the running host keeps serving the old one.
		private static string[] Changes(Realm row, DeclaredRealm realm)
		{
			var changes = new List<string>();

			void Compare<T>(string field, T was, T now)
			{
				if (!EqualityComparer<T>.Default.Equals(was, now))
					changes.Add($"{field} {was} -> {now}");
			}

			Compare(nameof(Realm.Name), row.Name, realm.Name);
			Compare(nameof(Realm.IpAddress), row.IpAddress, realm.IpAddress);
			Compare(nameof(Realm.Port), row.Port, realm.Port);
			Compare(nameof(Realm.ChatServerPort), row.ChatServerPort, realm.ChatServerPort);
			Compare(nameof(Realm.CsPlayerAgentPort), row.CsPlayerAgentPort, realm.CsPlayerAgentPort);
			Compare(nameof(Realm.Type), row.Type, realm.Type);
			Compare(nameof(Realm.Language), row.Language, realm.Language);

			return [.. changes];
		}

		// ⚠️ The live columns are left at their defaults - Offline, never seen, Recommended - and are NOT settable from
		// the config. RealmLiveness reads a null heartbeat as down even when the flag says Online, so a realm declared
		// today does not appear up until a host has actually claimed it and said so.
		private static Realm Row(DeclaredRealm realm)
		{
			return new Realm
			{
				Id = realm.Id,
				Name = realm.Name,
				IpAddress = realm.IpAddress,
				Port = realm.Port,
				ChatServerPort = realm.ChatServerPort,
				CsPlayerAgentPort = realm.CsPlayerAgentPort,
				Type = realm.Type,
				Language = realm.Language
			};
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

			// One file, both dialects, nothing to run after it. There used to be a per-dialect counter-resync sidecar
			// (faolan.seq.{sqlite,postgres}.sql) for keys the store generates; no game-content table has one - every
			// one is keyed by its own identity - so both were empty and are gone. SqlSeedWriter refuses to emit a seed
			// that would need one.
			await ExecuteScriptAsync(db, seed, cancellationToken);

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

		/// <param name="realms">
		///     The realms the configuration declares (<c>FaolanRealms.Declared</c>), or null/empty for a deployment
		///     that declares none - which keeps the historical "insert the default realm into an empty table" path.
		/// </param>
		/// <param name="onlyRealmId">
		///     Reconcile just this realm's row, which a realm-role process passes so it writes only its own (#909).
		/// </param>
		public static async Task<(RealmReconciliation Realms, bool AccountCreated, bool DataSeeded)> InitializeAsync(DatabaseContext db, bool seedGameData = true,
			IReadOnlyList<DeclaredRealm>? realms = null, uint? onlyRealmId = null, CancellationToken cancellationToken = default)
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
			var reconciled = await ReconcileRealmsAsync(db, realms, onlyRealmId, cancellationToken);
			var accountCreated = await EnsureDefaultAccountAsync(db, cancellationToken);
			return (reconciled, accountCreated, dataSeeded);
		}
	}
}
