using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Cli;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Xunit;

namespace Faolan.Tests
{
	// Reconciling the declared realms into the Realms table: config owns the STATIC half of a row, the host that
	// serves the realm owns the live half, and nothing owns the right to delete one.
	//
	// ⭐ The line between those halves is what every gate here is really about. A realm row is three things wearing
	// one name - static deployment config, the live columns (OnlineStatus + LastSeenUtc, plus LoadStatus, which is
	// population), and the parent of four foreign keys - and only the first is declarable. A reconciler that reached
	// past that line would undo #909/#922 quietly: it would let a file claim a host is up.
	public class RealmReconciliationTests : IDisposable
	{
		private readonly string _path = Path.Combine(Path.GetTempPath(), $"faolan-realms-{Guid.NewGuid():N}.db");

		public void Dispose()
		{
			TestDb.Delete(_path);
		}

		[Fact]
		// Nothing declared, empty table: the historical default realm, inserted exactly as EnsureDefaultRealmAsync
		// always did.
		public async Task NothingDeclared_InsertsTheDefaultRealmIntoAnEmptyTable()
		{
			await using var db = await Empty();

			var result = await DatabaseInitializer.ReconcileRealmsAsync(db, null, cancellationToken: TestContext.Current.CancellationToken);

			Assert.Equal(["Project Faolan"], result.Created);
			var row = Assert.Single(db.Realms);
			Assert.Equal(FaolanRealms.Default[0].Port, row.Port);
		}

		[Fact]
		// ⭐ ...and nothing declared with a POPULATED table does nothing at all. This is what keeps every existing
		// deployment byte-identical: a database whose realm carries some other id (it was DB-generated) must not gain
		// a second realm from a declaration nobody made.
		public async Task NothingDeclared_LeavesAPopulatedTableAlone()
		{
			await using var db = await Empty();
			db.Realms.Add(Row(id: 42, name: "Hand Made", port: 9014));
			await db.SaveChangesAsync(TestContext.Current.CancellationToken);

			var result = await DatabaseInitializer.ReconcileRealmsAsync(db, null, cancellationToken: TestContext.Current.CancellationToken);

			Assert.True(result.IsEmpty);
			var row = Assert.Single(db.Realms);
			Assert.Equal(42u, row.Id);
		}

		[Fact]
		// A declared realm the database has never seen is created at the id the config gives - the id being explicit
		// is what lets a character FK'd to realm 2 survive the row being rebuilt.
		public async Task ADeclaredRealm_IsCreatedAtItsDeclaredId()
		{
			await using var db = await Empty();

			var result = await DatabaseInitializer.ReconcileRealmsAsync(db, [Declared(2, "Fury", port: 8014)], cancellationToken: TestContext.Current.CancellationToken);

			Assert.Equal(["Fury (id 2)"], result.Created);
			var row = Assert.Single(db.Realms);
			Assert.Equal(2u, row.Id);
			Assert.Equal(8014, row.Port);
		}

		[Fact]
		// ⚠️ A realm CREATED here is not a realm anybody said is up. It starts Offline with no heartbeat, which
		// RealmLiveness reads as down even if the flag said otherwise - so declaring a realm never advertises a host
		// that has not claimed it.
		public async Task ACreatedRealm_StartsOfflineWithNoHeartbeat()
		{
			await using var db = await Empty();

			await DatabaseInitializer.ReconcileRealmsAsync(db, [Declared(2, "Fury")], cancellationToken: TestContext.Current.CancellationToken);

			var row = Assert.Single(db.Realms);
			Assert.Equal(RealmOnlineStatus.Offline, row.OnlineStatus);
			Assert.Null(row.LastSeenUtc);
			Assert.False(RealmLiveness.IsServing(row.OnlineStatus, row.LastSeenUtc, DateTimeOffset.UtcNow));
		}

		[Fact]
		public async Task AChangedPort_IsWrittenAndReportedAsWasIs()
		{
			await using var db = await Empty();
			db.Realms.Add(Row(id: 2, name: "Fury", port: 8014));
			await db.SaveChangesAsync(TestContext.Current.CancellationToken);

			var result = await DatabaseInitializer.ReconcileRealmsAsync(db, [Declared(2, "Fury", port: 9014)], cancellationToken: TestContext.Current.CancellationToken);

			Assert.Equal(9014, Assert.Single(db.Realms).Port);
			Assert.Contains("Port 8014 -> 9014", Assert.Single(result.Updated));
		}

		[Fact]
		// ⭐ The gate this whole file exists for. Config rewrites the static columns of a realm that is CURRENTLY
		// SERVING and must not touch the three live ones - a file cannot observe that a host is up, and LoadStatus is
		// a population count nothing computes yet. Writing any of them here would make every restart overwrite what
		// the live world knows.
		public async Task ReconcilingARunningRealm_NeverTouchesTheLiveColumns()
		{
			var seen = DateTimeOffset.UtcNow.AddSeconds(-5);
			await using var db = await Empty();
			var live = Row(id: 2, name: "Fury", port: 8014);
			live.OnlineStatus = RealmOnlineStatus.Online;
			live.LastSeenUtc = seen;
			live.LoadStatus = RealmLoadStatus.Heavy;
			db.Realms.Add(live);
			await db.SaveChangesAsync(TestContext.Current.CancellationToken);

			await DatabaseInitializer.ReconcileRealmsAsync(db, [Declared(2, "Fury Reborn", port: 9014)], cancellationToken: TestContext.Current.CancellationToken);

			var row = Assert.Single(db.Realms);
			Assert.Equal("Fury Reborn", row.Name);
			Assert.Equal(RealmOnlineStatus.Online, row.OnlineStatus);
			Assert.Equal(seen, row.LastSeenUtc);
			Assert.Equal(RealmLoadStatus.Heavy, row.LoadStatus);
		}

		[Fact]
		// Idempotent: a second pass over an unchanged config reports nothing, so a restart does not log a change it
		// did not make.
		public async Task ASecondPass_ReportsNothing()
		{
			await using var db = await Empty();
			DeclaredRealm[] declared = [Declared(2, "Fury")];

			await DatabaseInitializer.ReconcileRealmsAsync(db, declared, cancellationToken: TestContext.Current.CancellationToken);
			var again = await DatabaseInitializer.ReconcileRealmsAsync(db, declared, cancellationToken: TestContext.Current.CancellationToken);

			Assert.True(again.IsEmpty);
		}

		[Fact]
		// ⭐ A realm-role process passes its own id, which keeps "a realm process writes only its own row" (#909/#922)
		// true of the static columns too. Realm 3 is declared and absent, and must STAY absent - this process has no
		// business creating another host's row.
		public async Task ScopingToOneRealm_WritesOnlyThatRow()
		{
			await using var db = await Empty();

			var result = await DatabaseInitializer.ReconcileRealmsAsync(db, [Declared(2, "Fury"), Declared(3, "Crom", port: 9014, chat: 9012, cs: 9001)], onlyRealmId: 2, cancellationToken: TestContext.Current.CancellationToken);

			Assert.Equal(["Fury (id 2)"], result.Created);
			Assert.Equal([2u], db.Realms.Select(r => r.Id).ToArray());
		}

		[Fact]
		// ...and it says nothing about the realms it did not look at. A scoped process knows only its own row, so
		// reporting the others as undeclared would be a guess.
		public async Task ScopingToOneRealm_ReportsNoUndeclaredRows()
		{
			await using var db = await Empty();
			db.Realms.Add(Row(id: 9, name: "Someone Else", port: 9014));
			await db.SaveChangesAsync(TestContext.Current.CancellationToken);

			var result = await DatabaseInitializer.ReconcileRealmsAsync(db, [Declared(2, "Fury")], onlyRealmId: 2, cancellationToken: TestContext.Current.CancellationToken);

			Assert.Empty(result.Undeclared);
		}

		[Fact]
		// ⚠️ A row the config has dropped is REPORTED, never deleted. Characters, guilds, mail and auction listings FK
		// a realm, so removing one is a data migration - and a config edit that silently orphaned a server's worth of
		// characters would be the worst possible way to find that out.
		public async Task AnUndeclaredRow_IsReportedAndLeftStanding()
		{
			await using var db = await Empty();
			db.Realms.Add(Row(id: 9, name: "Retired", port: 9014));
			await db.SaveChangesAsync(TestContext.Current.CancellationToken);

			var result = await DatabaseInitializer.ReconcileRealmsAsync(db, [Declared(2, "Fury")], cancellationToken: TestContext.Current.CancellationToken);

			Assert.Equal(["Retired (id 9)"], result.Undeclared);
			Assert.Contains(db.Realms, r => r.Id == 9);
		}

		private static DeclaredRealm Declared(uint id, string name, ushort port = 8014, ushort chat = 8012, ushort cs = 8001)
		{
			return new DeclaredRealm(id, name, "127.0.0.1", port, chat, cs, RealmType.PvP, RealmLanguage.English);
		}

		private static Realm Row(uint id, string name, ushort port)
		{
			return new Realm
			{
				Id = id,
				Name = name,
				IpAddress = "127.0.0.1",
				Port = port,
				ChatServerPort = 8012,
				CsPlayerAgentPort = 8001,
				Type = RealmType.PvP,
				Language = RealmLanguage.English
			};
		}

		private async Task<DatabaseContext> Empty()
		{
			var db = new DatabaseContext(TestDb.Options(_path));
			await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

			return db;
		}
	}
}
