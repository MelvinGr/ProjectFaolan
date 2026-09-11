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
	/// <summary>
	///     Writes the game data as a dialect-neutral SQL seed: one file that loads into both SQLite and Postgres, so
	///     the schema (EF migrations) plus this file can replace the committed binary faolan.db.
	/// </summary>
	/// <remarks>
	///     The seed is generated from the EF model rather than by <c>sqlite3 &lt;db&gt;.dump</c>, because a dump is
	///     SQLite-only in four ways that each break Postgres: identifiers are unquoted, so <c>insert into Items</c>
	///     folds to <c>items</c> while EF creates a case-preserved <c>"Items"</c>; inserts are positional, so the
	///     column order has to match across providers; booleans are emitted as 0/1, which Postgres rejects for a
	///     <c>boolean</c> column; and PRAGMA / sqlite_sequence statements have no Postgres equivalent. So this writer
	///     quotes identifiers, emits an explicit column list per row, and renders literals from each property's CLR
	///     type rather than from SQLite's storage affinity.
	///     Tables are emitted in foreign-key dependency order, so the file loads with constraints on rather than
	///     needing a disable-checks prologue - itself the least portable part of a dump (<c>PRAGMA foreign_keys=off</c>
	///     versus <c>set session_replication_role = replica</c>, the latter needing superuser).
	///     The result is one file with no per-dialect sidecar: no game-content table may have a store-generated key,
	///     so there are no auto-increment counters left to resync after the seed's explicit ids.
	/// </remarks>
	public static class SqlSeedWriter
	{
		/// <remarks>
		///     The seed carries game content. These roots are player or deployment state - per install, not shipped -
		///     and so is every table that hangs off one of them; the closure is derived from the model's foreign keys,
		///     so it cannot go stale the way a hardcoded list would. Accounts and Realms are deployment state:
		///     DatabaseInitializer creates the defaults for a new database.
		///     This boundary is what makes a content upgrade tractable. With player state out, every seeded table
		///     referenced from outside the seed has a natural, stable key (Items, Spells, QuestDefinitions,
		///     QuestGoalDefinitions, RespawnPoints, Maps), so a live character's references survive a refresh by
		///     construction. The generated-id tables that remain (WorldObjects, ConversationNodes,...) are internal to
		///     the seed - nothing outside points into them, so renumbering them on a regeneration is harmless.
		/// </remarks>
		private static readonly string[] ExcludedRoots =
		[
			"Characters", "Accounts", "Realms", "Guilds", "Mails", "ChatMessages", "TradepostListings", "AccountClaims"
		];

		/// <remarks>
		///     Rows per multi-row insert. The column list is the expensive part of this format - for narrow tables it
		///     outweighs the values themselves - so it is paid once per batch. Both dialects accept
		///     <c>values (..),(..)</c>; batching keeps any parser's memory bounded. Each row still gets its own line:
		///     faolan.sql is a reviewed artifact and a multi-megabyte single line would make its diffs useless.
		/// </remarks>
		private const int BatchRows = 1000;

		/// <summary>
		///     The content tables the seed covers, in foreign-key dependency order (principals first) with player and
		///     deployment state excluded.
		/// </summary>
		/// <remarks>
		///     Shared with <see cref="SqlSeedApplier" /> so the writer and the merge cannot disagree about what "the
		///     seed" is, the exclusion set especially.
		///     Grouped by table, not per entity type: two entity types can share one table (EF table splitting - Items
		///     and Abilities each split their Key1..Key4 off into a second type). Treating them separately would see
		///     each row twice, each time with only that type's share of the columns.
		/// </remarks>
		public static List<IGrouping<string, IEntityType>> ContentTables(DatabaseContext db)
		{
			var tables = OrderByDependency(db.Model.GetEntityTypes()
				.Where(e => e.GetTableName() != null)
				.GroupBy(e => e.GetTableName()!)
				.ToList());
			var excluded = PlayerStateTables(tables);
			return tables.Where(g => !excluded.Contains(g.Key) && g.Key != "__EFMigrationsHistory").ToList();
		}

		/// <summary>
		///     The columns of a table, unioned across the entity types that share it, and their CLR types.
		/// </summary>
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

		/// <summary>
		///     The primary-key columns, which the merge keys rows on.
		/// </summary>
		public static List<string> KeyOf(IGrouping<string, IEntityType> group)
		{
			var entity = group.First();
			var storeObject = StoreObjectIdentifier.Table(group.Key, entity.GetSchema());
			return entity.FindPrimaryKey()!.Properties.Select(p => p.GetColumnName(storeObject)!).ToList();
		}

		public static (int Tables, int Rows) Write(DatabaseContext db, string bodyPath)
		{
			var tables = ContentTables(db);

			var body = new StringBuilder();
			body.AppendLine("-- Faolan game data. GENERATED - see SqlSeedWriter (#411); do not hand-edit.");
			body.AppendLine("-- Loads into SQLite and Postgres alike: create the schema first (dotnet ef database update),");
			body.AppendLine("-- then run this. It is the WHOLE seed - there is nothing to run after it.");
			body.AppendLine("-- Player and deployment state is excluded on purpose (characters, accounts, realms, guilds,");
			body.AppendLine("-- mail, chat, auction listings, claims) - this file is GAME CONTENT.");
			body.AppendLine("BEGIN TRANSACTION;");

			var rowTotal = 0;
			var written = 0;
			foreach (var group in tables)
			{
				var table = group.Key;
				var entity = group.First();
				var storeObject = StoreObjectIdentifier.Table(table, entity.GetSchema());
				var (columns, clrTypes) = ColumnsOf(group);

				var rows = ReadRows(db, table, columns, KeyOf(group));
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

				// The seed inserts every id explicitly and there is no counter-resync file, so no game-content table
				// may key on a store-generated column (every one is keyed by its identity - see the model conventions
				// in CLAUDE.md). This is the generator's half of that invariant: if one reappears, throw naming the
				// table rather than emit a seed whose next insert collides.
				var identity = entity.FindPrimaryKey()?.Properties.FirstOrDefault(p => p.ValueGenerated != ValueGenerated.Never);
				if (identity != null && rows.Count > 0)
				{
					throw new InvalidOperationException(
						$"Game-content table '{table}' keys on a store-generated column ('{identity.GetColumnName(storeObject)}', {identity.ValueGenerated}), which the seed cannot " +
						"restore: it inserts ids explicitly and there is no counter-resync file any more. Give the table a natural or derived key " +
						"(.ValueGeneratedNever() + a migration) - see the model conventions in CLAUDE.md.");
				}
			}

			body.AppendLine("COMMIT;");
			File.WriteAllText(bodyPath, body.ToString());
			return (written, rowTotal);
		}

		/// <param name="orderBy">
		///     The table's primary-key columns. Passing them makes the emit deterministic, without which the seed is
		///     worthless as a reviewable mirror: an unordered SELECT returns rows in whatever order the storage engine
		///     happens to hold them, which is stable only until something rewrites a page, so unrelated rows move and
		///     churn faolan.sql - or worse, hide a real row change in a diff nobody can read. Ordering by the primary
		///     key (not by rowid, which Postgres does not have) also means the two providers emit the same file, which
		///     the "loads into both" promise needs.
		/// </param>
		public static List<object?[]> ReadRows(DatabaseContext db, string table, List<string> columns, List<string>? orderBy = null)
		{
			var connection = db.Database.GetDbConnection();
			if (connection.State != System.Data.ConnectionState.Open)
				connection.Open();

			var order = orderBy is { Count: > 0 }
				? $" ORDER BY {string.Join(", ", orderBy.Select(Quote))}"
				: "";

			using var command = connection.CreateCommand();
			command.CommandText = $"SELECT {string.Join(", ", columns.Select(Quote))} FROM {Quote(table)}{order}";
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

		/// <summary>
		///     Renders a literal that a Postgres parser and a SQLite parser read identically.
		/// </summary>
		/// <remarks>
		///     Rendered from the model's CLR type, never from the value the reader hands back. SQLite has no boolean
		///     storage class, so its provider returns a <c>long</c> 0/1 for a bool property; switching on the runtime
		///     type would emit <c>0</c>, which Postgres rejects ("column is of type boolean but expression is of type
		///     integer"). Floats have the same trap - they come back as <c>double</c>.
		/// </remarks>
		private static string Literal(object? value, Type clrType)
		{
			if (value is null or DBNull)
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

			// Round-trip ("R") so re-loading is bit-identical rather than merely close, and widen a float property to
			// double rather than narrowing to single. A float column holds the double widening of its float (0.3f is
			// stored as 0.30000001192092896); rendering it as single gives "0.3", which reloads as the double 0.3 - a
			// different value, and one a tolerance-based check cannot see. Emitting the stored double reproduces the
			// exact bits on SQLite, and Postgres rounds it back to the same float4 on the way into a `real` column.
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

		/// <remarks>
		///     Double quotes are the SQL standard for identifiers and mean the same thing in both dialects - without
		///     them Postgres folds to lower case and stops finding EF's case-preserved tables.
		/// </remarks>
		public static string Quote(string identifier)
		{
			return $"\"{identifier}\"";
		}

		/// <summary>
		///     The excluded roots plus everything that reaches one of them through foreign keys.
		/// </summary>
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

		/// <summary>
		///     The tables this one points at through foreign keys, self-references dropped.
		/// </summary>
		private static IEnumerable<string> Principals(IGrouping<string, IEntityType> group)
		{
			return group
				.SelectMany(e => e.GetForeignKeys())
				.Select(fk => fk.PrincipalEntityType.GetTableName()!)
				.Where(principal => principal != group.Key)
				.Distinct();
		}

		/// <summary>
		///     Orders tables principals-before-dependents, so the file loads with foreign keys enforced.
		/// </summary>
		/// <remarks>
		///     Self-references are skipped: they order rows, not tables, and the two self-referencing tables here
		///     (PatrolNode, ConversationNode) are inserted in id order, which is also insertion order.
		/// </remarks>
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
