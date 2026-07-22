using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// Counts every mapped table so the before/after delta is complete. Derived from the EF model, so new
	// tables are picked up automatically. Counts are
	// read on the same connection the runner mutates, so inside the dry-run transaction they reflect the
	// pending (soon-to-be-rolled-back) changes - exactly the delta we want to show.
	public static class TableSnapshot
	{
		public static async Task<Dictionary<string, int>> CaptureAsync(DatabaseContext db)
		{
			var tables = db.Model.GetEntityTypes().Select(e => e.GetTableName()).Where(t => !string.IsNullOrEmpty(t)).Distinct().OrderBy(t => t).ToList();

			var counts = new Dictionary<string, int>();
			foreach (var table in tables)
			{
				// Table names come from the trusted EF model (not user input) and a table identifier can't be a SQL
				// parameter anyway, so the interpolation is safe - suppress the raw-SQL injection advisory.
#pragma warning disable EF1002
				// Alias quoted ("Value") so Postgres keeps the case EF's scalar wrapper expects; an unquoted alias
				// folds to l-case there and the wrapper's "Value" lookup fails. SQLite accepts the quoted alias too.
				var count = await db.Database.SqlQueryRaw<int>($"SELECT COUNT(*) AS \"Value\" FROM \"{table}\"").SingleAsync();
#pragma warning restore EF1002
				counts[table!] = count;
			}

			return counts;
		}

		// Prints the changed rows (and a total). Unchanged tables are omitted to keep the report readable.
		public static void PrintDelta(Dictionary<string, int> before, Dictionary<string, int> after, TextWriter @out)
		{
			@out.WriteLine("\n  table                          before     after    delta");
			@out.WriteLine("  -------------------------------------------------------");
			var any = false;
			foreach (var (table, a) in after.OrderBy(kv => kv.Key))
			{
				var b = before.GetValueOrDefault(table);
				if (a == b)
					continue;

				any = true;
				var delta = a - b;
				@out.WriteLine($"  {table,-28} {b,8} {a,9} {(delta >= 0 ? "+" : "")}{delta,6}");
			}

			if (!any)
				@out.WriteLine("  (no row-count changes)");
		}
	}
}
