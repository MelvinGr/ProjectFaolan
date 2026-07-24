using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using Faolan.Core.Database;
using Faolan.Core.Database.Extensions;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Faolan.Importer.Captures
{
	// Upgrades the game CONTENT of a database that already has data, from faolan.sql (#412). The seed loads itself
	// into an empty database (DatabaseInitializer), but that is keyed on emptiness - pulling a newer seed does
	// nothing to a database you have already been playing on. This is the missing path.
	//
	// It is a MERGE, not a truncate-and-reload. Per table: INSERT what is new, UPDATE what changed, DELETE what the
	// seed no longer has - keyed on the primary key. That works precisely because the seed is content-only: every
	// table it covers that is referenced from OUTSIDE it (Items, Spells, QuestDefinitions, QuestGoalDefinitions,
	// RespawnPoints, Maps) has a NATURAL, stable key, so a live character's inventory item or bound respawn point
	// keeps pointing at the same row. The generated-id tables that remain in the seed (WorldObjects, ...) are
	// internal to it - nothing outside points into them, so their renumbering is invisible. The conversation tree
	// is no longer among them: its keys are derived from each node's identity and each child's position, so a
	// re-harvest reproduces them exactly and this merge sees only genuine content changes.
	//
	// The one thing a merge cannot decide for you is a row the new seed DROPS while something still references it -
	// a character holding an item the catalog no longer has. That is reported and the upgrade REFUSES; silently
	// deleting the character's row or nulling the reference are both policy choices that belong to a human.
	public static class SqlSeedApplier
	{
		public sealed record TableChange(string Table, int Inserted, int Updated, int Deleted)
		{
			public bool Any => Inserted + Updated + Deleted > 0;
		}

		public sealed record Report(List<TableChange> Changes, List<string> Blockers)
		{
			public bool Blocked => Blockers.Count > 0;
			public int Inserted => Changes.Sum(c => c.Inserted);
			public int Updated => Changes.Sum(c => c.Updated);
			public int Deleted => Changes.Sum(c => c.Deleted);
		}

		public static Report Apply(DatabaseContext target, string seedPath, bool dryRun)
		{
			// The seed is SQL, so the desired state is materialised by LOADING it - into a throwaway SQLite database
			// rather than parsing the file. Same statements the real load runs, so the two cannot diverge.
			var staging = Path.Combine(Path.GetTempPath(), $"faolan-seed-{Guid.NewGuid():N}.db");
			try
			{
				using var desired = new DatabaseContext(DatabaseExtensions.BuildOptions(FaolanDbProvider.Sqlite, $"Data Source={staging}"));
				desired.Database.Migrate();
				ExecuteScript(desired, seedPath);

				var tables = SqlSeedWriter.ContentTables(target);
				var changes = new List<TableChange>();
				var blockers = new List<string>();
				var plans = new List<(IGrouping<string, IEntityType> Group, List<object?[]> Insert, List<object?[]> Update, List<object?[]> Delete, List<string> Columns, List<string> Key)>();

				foreach (var group in tables)
				{
					var (columns, _) = SqlSeedWriter.ColumnsOf(group);
					var key = SqlSeedWriter.KeyOf(group);
					var keyIndexes = key.Select(c => columns.IndexOf(c)).ToArray();

					var desiredRows = Index(SqlSeedWriter.ReadRows(desired, group.Key, columns), keyIndexes);
					var currentRows = Index(SqlSeedWriter.ReadRows(target, group.Key, columns), keyIndexes);

					var insert = desiredRows.Where(r => !currentRows.ContainsKey(r.Key)).Select(r => r.Value).ToList();
					var delete = currentRows.Where(r => !desiredRows.ContainsKey(r.Key)).Select(r => r.Value).ToList();
					var update = desiredRows
						.Where(r => currentRows.TryGetValue(r.Key, out var current) && !SameRow(current, r.Value))
						.Select(r => r.Value)
						.ToList();

					changes.Add(new TableChange(group.Key, insert.Count, update.Count, delete.Count));
					plans.Add((group, insert, update, delete, columns, key));

					// A dropped row that something outside the seed still points at is the one case that needs a human.
					if (delete.Count > 0)
						blockers.AddRange(ReferencesTo(target, group, delete, key, columns));
				}

				var report = new Report(changes, blockers);
				if (dryRun || report.Blocked)
					return report;

				using var transaction = target.Database.BeginTransaction();

				// Principals before dependents for the additive half, and the reverse for deletes, so foreign keys
				// hold at every step and the upgrade never needs constraints turned off.
				foreach (var (group, insert, update, _, columns, key) in plans)
				{
					Write(target, group.Key, columns, key, insert, insertRow: true);
					Write(target, group.Key, columns, key, update, insertRow: false);
				}

				foreach (var (group, _, _, delete, columns, key) in Enumerable.Reverse(plans))
					Delete(target, group.Key, columns, key, delete);

				transaction.Commit();
				return report;
			}
			finally
			{
				foreach (var file in new[] { staging, $"{staging}-wal", $"{staging}-shm" })
				{
					if (File.Exists(file))
						File.Delete(file);
				}
			}
		}

		// Rows that would be deleted but are still referenced from a table the seed does NOT own (i.e. player state).
		private static IEnumerable<string> ReferencesTo(DatabaseContext target, IGrouping<string, IEntityType> group, List<object?[]> deleting, List<string> key,
			List<string> columns)
		{
			// Only single-column keys can be referenced by a foreign key here, which is every case in this schema.
			if (key.Count != 1)
				yield break;

			var content = SqlSeedWriter.ContentTables(target).Select(g => g.Key).ToHashSet();
			var keyIndex = columns.IndexOf(key[0]);
			var doomed = deleting.Select(r => r[keyIndex]).Where(v => v != null).ToList();
			if (doomed.Count == 0)
				yield break;

			foreach (var entity in target.Model.GetEntityTypes())
			{
				var table = entity.GetTableName();
				if (table == null || content.Contains(table))
					continue; // inside the seed: it is replaced wholesale, so internal references move with it

				var storeObject = StoreObjectIdentifier.Table(table, entity.GetSchema());
				foreach (var foreignKey in entity.GetForeignKeys().Where(f => f.PrincipalEntityType.GetTableName() == group.Key))
				{
					var column = foreignKey.Properties[0].GetColumnName(storeObject);
					if (column == null)
						continue;

					var hits = CountReferences(target, table, column, doomed);
					if (hits > 0)
						yield return $"{table}.{column} still references {hits} row(s) that the new seed drops from {group.Key}";
				}
			}
		}

		private static int CountReferences(DatabaseContext db, string table, string column, List<object?> values)
		{
			using var command = Command(db);
			var parameters = values.Select((v, i) => Parameter(command, $"@p{i}", v)).ToList();
			command.CommandText =
				$"SELECT COUNT(*) FROM {SqlSeedWriter.Quote(table)} WHERE {SqlSeedWriter.Quote(column)} IN ({string.Join(",", parameters)})";
			return Convert.ToInt32(command.ExecuteScalar());
		}

		private static void Write(DatabaseContext db, string table, List<string> columns, List<string> key, List<object?[]> rows, bool insertRow)
		{
			foreach (var row in rows)
			{
				using var command = Command(db);
				if (insertRow)
				{
					var names = string.Join(", ", columns.Select(c => SqlSeedWriter.Quote(c)));
					var values = string.Join(", ", columns.Select((_, i) => Parameter(command, $"@v{i}", row[i])));
					command.CommandText = $"INSERT INTO {SqlSeedWriter.Quote(table)} ({names}) VALUES ({values})";
				}
				else
				{
					var sets = columns.Where(c => !key.Contains(c)).Select(c => $"{SqlSeedWriter.Quote(c)} = {Parameter(command, $"@v{columns.IndexOf(c)}", row[columns.IndexOf(c)])}");
					command.CommandText = $"UPDATE {SqlSeedWriter.Quote(table)} SET {string.Join(", ", sets)} WHERE {Where(command, key, columns, row)}";
				}

				command.ExecuteNonQuery();
			}
		}

		private static void Delete(DatabaseContext db, string table, List<string> columns, List<string> key, List<object?[]> rows)
		{
			foreach (var row in rows)
			{
				using var command = Command(db);
				command.CommandText = $"DELETE FROM {SqlSeedWriter.Quote(table)} WHERE {Where(command, key, columns, row)}";
				command.ExecuteNonQuery();
			}
		}

		private static string Where(DbCommand command, List<string> key, List<string> columns, object?[] row)
		{
			return string.Join(" AND ", key.Select(k => $"{SqlSeedWriter.Quote(k)} = {Parameter(command, $"@k{key.IndexOf(k)}", row[columns.IndexOf(k)])}"));
		}

		private static string Parameter(DbCommand command, string name, object? value)
		{
			var parameter = command.CreateParameter();
			parameter.ParameterName = name;
			parameter.Value = value ?? DBNull.Value;
			command.Parameters.Add(parameter);
			return name;
		}

		private static DbCommand Command(DatabaseContext db)
		{
			var connection = db.Database.GetDbConnection();
			if (connection.State != ConnectionState.Open)
				connection.Open();

			var command = connection.CreateCommand();
			command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
			return command;
		}

		private static Dictionary<string, object?[]> Index(List<object?[]> rows, int[] keyIndexes)
		{
			return rows.ToDictionary(r => string.Join('', keyIndexes.Select(i => Convert.ToString(r[i], System.Globalization.CultureInfo.InvariantCulture))), r => r);
		}

		private static bool SameRow(object?[] left, object?[] right)
		{
			for (var i = 0; i < left.Length; i++)
			{
				if (left[i] is byte[] a && right[i] is byte[] b)
				{
					if (!a.AsSpan().SequenceEqual(b))
						return false;
				}
				else if (!Equals(left[i], right[i]))
				{
					return false;
				}
			}

			return true;
		}

		private static void ExecuteScript(DatabaseContext db, string path)
		{
			var connection = db.Database.GetDbConnection();
			if (connection.State != ConnectionState.Open)
				connection.Open();

			using var command = connection.CreateCommand();
			command.CommandText = File.ReadAllText(path);
			command.CommandTimeout = 0;
			command.ExecuteNonQuery();
		}
	}
}
