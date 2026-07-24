using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Faolan.Core.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Faolan.Importer.Captures
{
	// Writes the game DATA as a dialect-neutral SQL seed (#411): one file that loads into BOTH SQLite and Postgres,
	// so the schema (EF migrations) + this file can eventually replace the committed binary faolan.db (#379).
	//
	// `sqlite3 <db> .dump` cannot do this. Measured against the pre-#411 dump, all of it breaks Postgres:
	//   - identifiers unquoted -> `INSERT INTO Items` folds to `items`, but EF creates `"Items"` case-preserved
	//   - POSITIONAL inserts -> the column ORDER has to match, which it does not across providers (and did not even
	//     across two SQLite schemas until the migrations were squashed)
	//   - booleans as 0/1 -> Postgres rejects those for a `boolean` column (60 of them)
	//   - `PRAGMA`/`sqlite_sequence` statements -> SQLite-only
	// So the seed is GENERATED from the EF model instead: quoted identifiers, an explicit column list per row, and
	// literals rendered from each property's CLR type rather than from SQLite's storage affinity.
	//
	// Tables are emitted in FOREIGN-KEY DEPENDENCY ORDER, so the file loads with constraints ON rather than needing
	// a disable-checks prologue - which is itself the least portable part of a dump (`PRAGMA foreign_keys=OFF` vs
	// `SET session_replication_role = replica`, the latter needing superuser).
	//
	// The only genuinely per-dialect part left is resyncing the auto-increment counters after inserting explicit
	// ids, so that goes in a small SIDECAR file per dialect and the big body stays 100% common.
	public static class SqlSeedWriter
	{
		// The seed carries GAME CONTENT. Everything below is PLAYER or DEPLOYMENT state - per install, not shipped -
		// and so is every table that hangs off one of them (the closure is derived from the model's foreign keys, so
		// it cannot go stale the way a hardcoded list would; these roots pull in 20 more, the CharacterX tables and
		// MailAttachments).
		//
		// `Characters` was the only root until #412: the character-FK closure was standing in for "player state" and
		// missed everything that is not FK-linked to a character, so a fresh clone came up carrying 243 live auction
		// listings, 19 account claims, someone's mail and a chat message. Accounts and Realms are deployment state -
		// DatabaseInitializer creates the defaults for a new database, which is why they do not belong in the data.
		//
		// This boundary is also what makes a content UPGRADE tractable: with player state out, every seeded table
		// referenced from OUTSIDE the seed has a natural, stable key (Items, Spells, QuestDefinitions,
		// QuestGoalDefinitions, RespawnPoints, Maps), so a live character's references survive a refresh by
		// construction. The generated-id tables that remain (WorldObjects, ConversationNodes, ...) are internal to
		// the seed - nothing outside points into them, so renumbering them on a regeneration is harmless.
		private static readonly string[] ExcludedRoots =
		[
			"Characters", "Accounts", "Realms", "Guilds", "Mails", "ChatMessages", "TradepostListings", "AccountClaims"
		];

		// Rows per multi-row INSERT. The column list is the expensive part of this format - repeating it per row cost
		// 16 MB of the 24 (68%), more than the values themselves for narrow tables - so it is paid once per BATCH
		// instead. Both dialects accept `VALUES (..),(..)`; SQLite swallows 100k rows in one statement, but batching
		// keeps any parser's memory bounded. Each row still gets its OWN LINE: faolan.sql is a reviewed artifact and
		// a multi-megabyte single line would make its diffs useless.
		private const int BatchRows = 1000;

		// The content tables the seed covers, in FOREIGN-KEY DEPENDENCY ORDER (principals first) with the player and
		// deployment state excluded. Shared with SqlSeedApplier so the writer and the merge cannot disagree about
		// what "the seed" is - the exclusion set especially.
		//
		// GROUPED BY TABLE, not per entity type: two entity types can share one table (EF table splitting - here
		// Items and Abilities each split their Key1..Key4 off into a second type). Treating them separately would
		// see each row TWICE, each time with only that type's share of the columns.
		public static List<IGrouping<string, IEntityType>> ContentTables(DatabaseContext db)
		{
			var tables = OrderByDependency(db.Model.GetEntityTypes()
				.Where(e => e.GetTableName() != null)
				.GroupBy(e => e.GetTableName()!)
				.ToList());
			var excluded = PlayerStateTables(tables);
			return tables.Where(g => !excluded.Contains(g.Key) && g.Key != "__EFMigrationsHistory").ToList();
		}

		// The columns of a table, unioned across the entity types that share it, and their CLR types.
		public static (List<string> Columns, Type[] ClrTypes) ColumnsOf(IGrouping<string, IEntityType> group)
		{
			var storeObject = StoreObjectIdentifier.Table(group.Key, group.First().GetSchema());
			var properties = group
				.SelectMany(e => e.GetProperties())
				.Where(p => p.GetColumnName(storeObject) != null)
				.GroupBy(p => p.GetColumnName(storeObject)!)
				.Select(g => g.First())
				.ToList();
			return (properties.Select(p => p.GetColumnName(storeObject)!).ToList(), properties.Select(p => p.ClrType).ToArray());
		}

		// The primary-key columns, which the merge keys rows on.
		public static List<string> KeyOf(IGrouping<string, IEntityType> group)
		{
			var entity = group.First();
			var storeObject = StoreObjectIdentifier.Table(group.Key, entity.GetSchema());
			return entity.FindPrimaryKey()!.Properties.Select(p => p.GetColumnName(storeObject)!).ToList();
		}

		public static (int Tables, int Rows) Write(DatabaseContext db, string bodyPath, string sqliteSidecarPath, string postgresSidecarPath)
		{
			var tables = ContentTables(db);

			var body = new StringBuilder();
			body.AppendLine("-- Faolan game data. GENERATED - see SqlSeedWriter (#411); do not hand-edit.");
			body.AppendLine("-- Loads into SQLite and Postgres alike: create the schema first (dotnet ef database update),");
			body.AppendLine("-- then run this, then the matching faolan.seq.<dialect>.sql sidecar.");
			body.AppendLine("-- Player and deployment state is excluded on purpose (characters, accounts, realms, guilds,");
			body.AppendLine("-- mail, chat, auction listings, claims) - this file is GAME CONTENT.");
			body.AppendLine("BEGIN TRANSACTION;");

			var rowTotal = 0;
			var written = 0;
			var maxIds = new List<(string Table, string Column, long Max)>();
			foreach (var group in tables)
			{
				var table = group.Key;
				var entity = group.First();
				var storeObject = StoreObjectIdentifier.Table(table, entity.GetSchema());
				var (columns, clrTypes) = ColumnsOf(group);

				var rows = ReadRows(db, table, columns);
				rowTotal += rows.Count;
				written++;

				var columnList = string.Join(", ", columns.Select(Quote));
				for (var start = 0; start < rows.Count; start += BatchRows)
				{
					var batch = rows.Skip(start).Take(BatchRows).ToList();
					body.AppendLine($"INSERT INTO {Quote(table)} ({columnList}) VALUES");
					for (var i = 0; i < batch.Count; i++)
					{
						var values = string.Join(", ", batch[i].Select((v, c) => Literal(v, clrTypes[c])));
						body.AppendLine($"({values}){(i == batch.Count - 1 ? ";" : ",")}");
					}
				}

				// A key the store generates needs its counter moved past the ids we just inserted explicitly.
				var identity = entity.FindPrimaryKey()?.Properties.FirstOrDefault(p => p.ValueGenerated == ValueGenerated.OnAdd);
				if (identity != null && rows.Count > 0)
				{
					var column = identity.GetColumnName(storeObject)!;
					var index = columns.IndexOf(column);
					var max = rows.Select(r => Convert.ToInt64(r[index], CultureInfo.InvariantCulture)).Max();
					maxIds.Add((table, column, max));
				}
			}

			body.AppendLine("COMMIT;");
			File.WriteAllText(bodyPath, body.ToString());
			File.WriteAllText(sqliteSidecarPath, SqliteSidecar(maxIds));
			File.WriteAllText(postgresSidecarPath, PostgresSidecar(maxIds));
			return (written, rowTotal);
		}

		// SQLite keeps AUTOINCREMENT high-water marks in sqlite_sequence; the row only exists once that table has
		// taken an auto id, so upsert it rather than assuming either state.
		private static string SqliteSidecar(List<(string Table, string Column, long Max)> maxIds)
		{
			var sb = new StringBuilder();
			sb.AppendLine("-- GENERATED (#411): SQLite half of the seed - the AUTOINCREMENT high-water marks.");
			sb.AppendLine("-- Run after faolan.sql. Pointless on a db with no AUTOINCREMENT tables; harmless there.");
			sb.AppendLine("BEGIN TRANSACTION;");
			foreach (var (table, _, max) in maxIds)
			{
				sb.AppendLine($"DELETE FROM sqlite_sequence WHERE name = '{table}';");
				sb.AppendLine($"INSERT INTO sqlite_sequence (name, seq) VALUES ('{table}', {max});");
			}

			sb.AppendLine("COMMIT;");
			return sb.ToString();
		}

		// Postgres keeps them in a sequence owned by the identity column; pg_get_serial_sequence finds it by name so
		// this does not hard-code the sequence names EF generated.
		private static string PostgresSidecar(List<(string Table, string Column, long Max)> maxIds)
		{
			var sb = new StringBuilder();
			sb.AppendLine("-- GENERATED (#411): Postgres half of the seed - advances each identity sequence past the");
			sb.AppendLine("-- explicit ids inserted by faolan.sql, so the next INSERT does not collide. Run after it.");
			sb.AppendLine("BEGIN;");
			foreach (var (table, column, max) in maxIds)
				// The column name goes in VERBATIM: EF creates it case-preserved ("Id"), and lower-casing it makes
				// pg_get_serial_sequence report `column "id" of relation ... does not exist`.
				sb.AppendLine($"SELECT setval(pg_get_serial_sequence('\"{table}\"', '{column}'), {max});");

			sb.AppendLine("COMMIT;");
			return sb.ToString();
		}

		public static List<object?[]> ReadRows(DatabaseContext db, string table, List<string> columns)
		{
			var connection = db.Database.GetDbConnection();
			if (connection.State != System.Data.ConnectionState.Open)
				connection.Open();

			using var command = connection.CreateCommand();
			command.CommandText = $"SELECT {string.Join(", ", columns.Select(Quote))} FROM {Quote(table)}";
			using var reader = command.ExecuteReader();
			var rows = new List<object?[]>();
			while (reader.Read())
			{
				var values = new object?[columns.Count];
				for (var i = 0; i < columns.Count; i++)
				{
					values[i] = reader.IsDBNull(i)
						? null
						: reader.GetValue(i);
				}

				rows.Add(values);
			}

			return rows;
		}

		// Literals a Postgres parser and a SQLite parser read IDENTICALLY.
		//
		// ⚠️ Rendered from the MODEL's CLR type, never from the value the reader hands back. SQLite has no boolean
		// storage class, so its provider returns a `long` 0/1 for a bool property - switching on the runtime type
		// therefore emits `0`, which Postgres rejects outright ("column is of type boolean but expression is of type
		// integer"). Same trap for floats, which come back as `double`.
		private static string Literal(object? value, Type clrType)
		{
			if (value is null || value is DBNull)
				return "NULL";

			var type = Nullable.GetUnderlyingType(clrType) ?? clrType;
			if (type.IsEnum)
				type = Enum.GetUnderlyingType(type);

			if (type == typeof(bool))
				return Convert.ToBoolean(value, CultureInfo.InvariantCulture)
					? "TRUE"
					: "FALSE";

			if (type == typeof(byte[]))
				throw new InvalidOperationException(
					"A byte[] column reached the SQL seed. There is no blob literal both dialects accept (SQLite x'..' vs Postgres '\\x..'), which is why #410 columnized every blob away - decode the new one or store it as text.");

			// Round-trip ("R") so re-loading is bit-identical rather than merely close - and note this widens a
			// `float` property to DOUBLE rather than narrowing to single.
			//
			// ⚠️ Narrowing loses data. A float column holds the double widening of its float (0.3f is stored as
			// 0.30000001192092896); rendering it as single gives "0.3", which reloads as the DOUBLE 0.3 - a
			// different value. It shifted 41731 rows by ~1e-8 and was invisible to a tolerance-based check.
			// Emitting the stored double reproduces the exact bits on SQLite, and Postgres rounds it back to the
			// same float4 on the way into a `real` column.
			if (type == typeof(float) || type == typeof(double))
				return Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);

			if (type == typeof(decimal))
				return Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);

			// Dates and guids arrive as the provider's own text form under SQLite; keep it verbatim (it is already
			// the round-trippable one) and only quote it.
			if (type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(Guid) || type == typeof(string))
				return $"'{Text(value).Replace("'", "''")}'";

			// Everything else is an integer of some width - render invariantly, never with a thousands separator.
			return value is IFormattable formattable
				? formattable.ToString(null, CultureInfo.InvariantCulture)
				: throw new InvalidOperationException($"No portable SQL literal for {value.GetType().Name}.");
		}

		private static string Text(object value)
		{
			return value switch
			{
				string s => s,
				DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
				DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture),
				_ => Convert.ToString(value, CultureInfo.InvariantCulture)!
			};
		}

		// Double quotes are the SQL standard for identifiers and mean the same thing in both dialects - without them
		// Postgres folds to lower case and stops finding EF's case-preserved tables.
		public static string Quote(string identifier)
		{
			return $"\"{identifier}\"";
		}

		// The excluded ROOTS plus everything that reaches one of them through foreign keys.
		private static HashSet<string> PlayerStateTables(List<IGrouping<string, IEntityType>> tables)
		{
			var excluded = new HashSet<string>(ExcludedRoots);
			bool grew;
			do
			{
				grew = false;
				foreach (var group in tables)
				{
					if (excluded.Contains(group.Key))
						continue;

					if (Principals(group).Any(excluded.Contains))
						grew = excluded.Add(group.Key);
				}
			} while (grew);

			return excluded;
		}

		// The tables this one points AT through foreign keys, self-references dropped.
		private static IEnumerable<string> Principals(IGrouping<string, IEntityType> group)
		{
			return group
				.SelectMany(e => e.GetForeignKeys())
				.Select(fk => fk.PrincipalEntityType.GetTableName()!)
				.Where(principal => principal != group.Key)
				.Distinct();
		}

		// Principals before dependents, so the file loads with foreign keys ENFORCED. Self-references are skipped -
		// they order rows, not tables, and the two self-referencing tables here (PatrolNode, ConversationNode) are
		// inserted in id order which is also insertion order.
		private static List<IGrouping<string, IEntityType>> OrderByDependency(List<IGrouping<string, IEntityType>> tables)
		{
			var ordered = new List<IGrouping<string, IEntityType>>();
			var placed = new HashSet<string>();
			var remaining = new List<IGrouping<string, IEntityType>>(tables.OrderBy(g => g.Key, StringComparer.Ordinal));

			while (remaining.Count > 0)
			{
				var ready = remaining.Where(g => Principals(g).All(placed.Contains)).ToList();

				// A dependency cycle across tables would loop forever; take the rest in name order and say so, rather
				// than hanging or silently dropping them.
				if (ready.Count == 0)
				{
					Console.WriteLine($"  warning: {remaining.Count} table(s) form a foreign-key cycle; emitting them in name order - the seed may need constraints deferred: {string.Join(", ", remaining.Select(g => g.Key))}");
					ready = remaining;
				}

				foreach (var group in ready)
				{
					ordered.Add(group);
					placed.Add(group.Key);
				}

				remaining.RemoveAll(ready.Contains);
			}

			return ordered;
		}
	}
}
