using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Faolan.Core.Rdb;

namespace Faolan.Importer.Cli
{
	// `--report data-table --report-arg <type>[:<id>]`: one of the client's `DataTable_c<T>` tables, as ROWS.
	//
	// #469's method is *"do NOT start from the bytes - start from the class name, then find the consumers"*, and that
	// is still right for naming a COLUMN. But it left every table needing a fresh hex read before the question could
	// even be asked, and most of them share one shape:
	//
	//     [type u32][a u32][version u32][count u32]  then  count x stride bytes
	//
	// with a handful carrying the count in the FIFTH word instead (1000509, 1000511). So the count word and the
	// stride are SOLVED here rather than assumed - the stride has to divide the body exactly for the chosen count,
	// and a table where neither candidate divides is REPORTED as such instead of being rendered against a guess.
	// That refusal matters: 1000505 does not divide because it is variable-width (it carries strings), and printing
	// it as fixed-width rows would have produced a plausible table of nonsense.
	//
	// ⚠️ It prints u32s because that is what these tables are made of, but a column that never exceeds a small value
	// is not necessarily a u32 field - `CoreTokenHolder` (1000520) carries its count as a BYTE. Read the loader's
	// parse callback before trusting a wide read on a table this refuses.
	public static partial class ReportCli
	{
		// The tables whose columns are decoded, so a run prints meanings rather than bare numbers
		// (`docs/rdb/client-data-tables.md`).
		private static readonly Dictionary<uint, string[]> DataTableColumns = new()
		{
			[1000507] = ["raceId", "classId"],
			[1000512] = ["playfieldId", "pvpRule"],
			[1065103] = ["level", "conRating"],
			[1000506] = ["archetypeId", "classId"],
			[1000071] = ["groupValue", "meshRdbId"]
		};

		// The two id->name registries the client ships, so a `raceId`/`classId`/`archetypeId` column prints what it
		// means. Both are variable-width `[id][len][name]` tables (1000505 carries a trailing tier word), which is
		// exactly the shape the fixed-width renderer below refuses - they are read here by a dedicated reader for
		// that reason rather than being forced through it.
		// Columns that are SIGNED. Everything in these tables reads as a u32 by default and almost all of them are
		// ids or counts, so a signed column is the exception and is listed rather than guessed - 1000071's group is
		// -1..-5 and printing it as 4294967292 is technically true and completely unreadable.
		private static readonly HashSet<string> SignedColumns = ["groupValue"];

		private const uint ClassRegistryType = 1000505;
		private const uint RaceRegistryType = 1000508;

		private static Dictionary<uint, string> NameRegistry(RdbReader rdb, uint type, bool trailingWord)
		{
			var map = new Dictionary<uint, string>();
			foreach (var entry in rdb.EntriesOfType(type).Where(rdb.CanRead))
			{
				var body = rdb.ReadDecompressed(entry);
				if (body.Length < 16)
					continue;

				var count = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12, 4));
				var at = 16;
				for (var i = 0; i < count && at + 8 <= body.Length; i++)
				{
					var id = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at, 4));
					var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at + 4, 4));
					at += 8;
					if (length < 0 || at + length > body.Length)
						break;

					map[id] = Encoding.Latin1.GetString(body, at, length);
					at += length + (trailingWord ? 4 : 0);
				}
			}

			return map;
		}

		private static void DataTable(string rdbDir, string arg)
		{
			var colon = arg.IndexOf(':');
			uint? onlyId = null;
			if (colon >= 0)
			{
				if (!uint.TryParse(arg[(colon + 1)..], out var parsed))
				{
					Console.WriteLine($"data-table: '{arg[(colon + 1)..]}' is not a numeric id (expected <type> or <type>:<id>)");
					return;
				}

				onlyId = parsed;
				arg = arg[..colon];
			}

			if (!uint.TryParse(arg, out var type))
			{
				Console.WriteLine($"data-table needs a numeric type in --report-arg, got '{arg}'");
				return;
			}

			using var rdb = OpenRdb(rdbDir);
			var classes = NameRegistry(rdb, ClassRegistryType, trailingWord: true);
			var races = NameRegistry(rdb, RaceRegistryType, trailingWord: false);

			foreach (var entry in rdb.EntriesOfType(type).Where(rdb.CanRead))
			{
				if (onlyId != null && entry.Id != onlyId)
					continue;

				var body = rdb.ReadDecompressed(entry);
				if (body.Length < 16)
					continue;

				Print(type, entry.Id, body, classes, races);
			}
		}

		private static void Print(uint type, uint entryId, byte[] body, Dictionary<uint, string> classes, Dictionary<uint, string> races)
		{
			var head = new uint[Math.Min(5, body.Length / 4)];
			for (var i = 0; i < head.Length; i++)
				head[i] = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(i * 4, 4));

			Console.WriteLine();
			Console.WriteLine($"type={type} id={entryId} {body.Length} B  head=[{string.Join(", ", head)}]");

			// ⭐ The count word and the HEADER LENGTH are both solved, not assumed, and they vary independently: most
			// tables are count-in-word-3 with a 16-byte header, 1000511 is count-in-word-4 with 20, and 1000071 is
			// count-in-word-3 with 20. Pairing them off as "word 3 means 16 bytes" silently refuses that last shape.
			foreach (var (countWord, headerBytes) in new[] { (3, 16), (3, 20), (4, 20) })
			{
				if (head.Length <= countWord)
					continue;

				var count = head[countWord];
				var rest = body.Length - headerBytes;
				if (count == 0 || count > 100_000 || rest <= 0 || rest % count != 0)
					continue;

				var stride = rest / (int)count;
				if (stride is < 4 or > 512 || stride % 4 != 0)
					continue;

				Rows(type, body, headerBytes, (int)count, stride, classes, races);
				return;
			}

			Console.WriteLine($"  no count word divides the body - this table is NOT fixed-width. "
			                  + $"Variable-width tables carry strings; try `--report rdb-dump --report-arg {type}` and read it. "
			                  + $"({(HasText(body) ? "it DOES contain text" : "no ASCII runs in it")})");
		}

		private static void Rows(uint type, byte[] body, int headerBytes, int count, int stride, Dictionary<uint, string> classes, Dictionary<uint, string> races)
		{
			var columns = stride / 4;
			var names = DataTableColumns.GetValueOrDefault(type);
			Console.WriteLine($"  {count} row(s) x {stride} B = {columns} u32 column(s)"
			                  + (names is null ? "   (columns not decoded - see docs/rdb/client-data-tables.md)" : ""));

			var header = new StringBuilder("  ").Append($"{"#",4}  ");
			for (var c = 0; c < columns; c++)
				header.Append($"{(names is not null && c < names.Length ? names[c] : $"w{c}"),20}");
			Console.WriteLine(header.ToString());

			// ⚠️ Capped, and it SAYS it is. A silent top-N over a 9,916-row table reads as "this is the table".
			const int Cap = 40;
			for (var r = 0; r < Math.Min(count, Cap); r++)
			{
				var line = new StringBuilder("  ").Append($"{r,4}  ");
				for (var c = 0; c < columns; c++)
				{
					var v = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(headerBytes + r * stride + c * 4, 4));
					// ⚠️ An id the registry does not carry prints as the NUMBER, never as a blank or a guess - the
					// second 1000507 entry is full of class ids that 1000505 does not list, and that absence is a
					// finding about the table rather than a rendering gap to paper over.
					var name = names is null || c >= names.Length ? null
						: names[c] switch
						{
							"classId" or "archetypeId" => classes.GetValueOrDefault(v),
							"raceId" => races.GetValueOrDefault(v),
							_ => null
						};
					var text = name ?? (names is not null && c < names.Length && SignedColumns.Contains(names[c])
						? ((int)v).ToString()
						: v.ToString());
					line.Append($"{text,20}");
				}

				Console.WriteLine(line.ToString());
			}

			if (count > Cap)
				Console.WriteLine($"  ... {count - Cap} more row(s) not shown");
		}

		private static bool HasText(byte[] body)
		{
			var run = 0;
			foreach (var b in body)
			{
				run = b is >= 0x20 and < 0x7F ? run + 1 : 0;
				if (run >= 4)
					return true;
			}

			return false;
		}
	}
}
