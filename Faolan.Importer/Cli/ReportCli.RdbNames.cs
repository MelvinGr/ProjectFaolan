using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Extensions;
using Faolan.Core.Rdb;

namespace Faolan.Importer.Cli
{
	// `--report rdb-names [--report-arg <id>|<name-substring>|index:<indexId>]`: the client's global id -> NAME
	// table (RDB 1000010/1, `RdbNameTable`).
	//
	// Three forms, because three different questions land here:
	//   * no arg          - the table's size and a sample, i.e. "is this shipped and does it parse".
	//   * `<id>`          - one id resolved, the common case when some record's payload holds a bare word.
	//   * `<substring>`   - every name containing it, for finding an id from a name you can guess at.
	//   * `index:<id>`    - the names an INDEX record of type 1020004 references (#331). Those records are
	//                       per-RDB-TYPE name lists: `index:1010029` is the SERVER-SIDE SCRIPT catalog, 7,102
	//                       names, and it is where the ambient-bark conversation groups live.
	//
	// ⚠️ Names come back LOWERCASED - the build folds case - so a name from here is the right identifier and the
	// wrong string to feed a case-sensitive hash. `RdbNameTable` carries the warning in full.
	public static partial class ReportCli
	{
		// The per-type name-index records (`1020004/<rdbType>`), which reference the name table by id.
		private const uint NameIndexType = 1020004;

		private static void RdbNames(string rdbDir, string? arg)
		{
			using var rdb = OpenRdb(rdbDir);
			var names = RdbNameTable.Load(rdb);
			Console.WriteLine($"rdb-names: {names.Count:N0} entries, {names.Values.Distinct().Count():N0} distinct (RDB {RdbNameTable.Type}/{RdbNameTable.RecordId}).");
			if (names.Count == 0)
			{
				Console.WriteLine("  the name table is not readable in this RDB - nothing else this report says means anything.");
				return;
			}

			if (string.IsNullOrWhiteSpace(arg))
			{
				foreach (var (id, name) in names.OrderBy(kv => kv.Key).Take(20))
					Console.WriteLine($"  {id,-10} {name}");

				Console.WriteLine("  ... pass an id, a name substring, or index:<rdbType> for one type's names.");
				return;
			}

			if (arg.StartsWith("index:", StringComparison.OrdinalIgnoreCase))
			{
				IndexedNames(rdb, names, arg["index:".Length..].Trim());
				return;
			}

			if (uint.TryParse(arg, out var wanted))
			{
				Console.WriteLine(names.TryGetValue(wanted, out var hit)
					? $"  {wanted} = {hit}"
					: $"  {wanted} is not in the name table.");
				return;
			}

			var needle = arg.ToLowerInvariant();
			var matches = names.Where(kv => kv.Value.Contains(needle, StringComparison.Ordinal)).OrderBy(kv => kv.Value).ToList();
			Console.WriteLine($"  {matches.Count:N0} name(s) contain '{needle}':");
			foreach (var (id, name) in matches.Take(200))
				Console.WriteLine($"  {id,-10} {name}");
		}

		// The names an index record references. It is read the robust way - every 4-byte-aligned word that IS a
		// name id - rather than by its record grammar, because the grammar is not decoded and the question ("which
		// names does this type declare") does not need it. A word that coincidentally equals a name id is possible;
		// with 187k ids in a 2^32 space that is ~0.004% per word, and the answer is a set of thousands.
		private static void IndexedNames(RdbReader rdb, IReadOnlyDictionary<uint, string> names, string arg)
		{
			if (!uint.TryParse(arg, out var indexId) || !rdb.TryGet(NameIndexType, indexId, out var entry) || !rdb.CanRead(entry))
			{
				Console.WriteLine($"  no {NameIndexType}/{arg} index record in this RDB.");
				return;
			}

			var body = rdb.ReadDecompressed(entry);
			var referenced = new List<string>();
			var seen = new HashSet<string>();
			for (var at = 0; at + 4 <= body.Length; at += 4)
			{
				if (names.TryGetValue(body.ReadU32Le(at), out var name) && seen.Add(name))
					referenced.Add(name);
			}

			var withExtension = referenced.Count(n => n.Contains('.', StringComparison.Ordinal));
			Console.WriteLine($"  {NameIndexType}/{indexId}: {referenced.Count:N0} distinct name(s) referenced " +
				$"({withExtension:N0} are FILEnames - a dot - and {referenced.Count - withExtension:N0} are bare identifiers).");
			foreach (var name in referenced.Where(n => !n.Contains('.', StringComparison.Ordinal)).Order())
				Console.WriteLine($"  {name}");
		}
	}
}
