using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Rdb;
using Faolan.Importer.Rdb;

namespace Faolan.Importer.Cli
{
	// `--report spell-recast [--report-arg <spellId>]`: where a spell's cooldown actually lives (#271).
	//
	// docs/re/spell-recast-source.md established that the base recast is `GameActionTypes.KeyValue` (0x2f9) on a
	// GameAction ELEMENT attached to the spell, fetched by the tooltip through the spell's virtual slot 0x84 - and
	// left "which record holds those elements" open, having eliminated the property run and field 321.
	//
	// It is the spell record's own ELEMENT region - the same region a feat's spell modifiers live in, read by the
	// same `ItemTemplateStatModifiers.ParseElements`. Nothing new had to be decoded; the region was simply never
	// looked at for a recast, because the search was framed as "which property FIELD is it" and a property field is
	// not what the client reads. `Life Leech` (220979) carries
	// `SetSpellLineRecastTime(line 41026, 3000)` = the 30 s its retail tooltip shows.
	public static partial class ReportCli
	{
		private const uint SpellRecordType = 1040005;

		// The four recast GameAction types (docs/re/gameaction-keylists.tsv). Two SET a recast and two ADJUST one;
		// only the setters are a spell's own base - `ChangeRecastTime` / `ChangeLineRecastTime` are the feat/AA
		// modifiers `FUN_007e1230` adds to a base the client already holds.
		private static readonly Dictionary<uint, string> RecastKinds = new()
		{
			[53226] = "SetSpellRecastTime",
			[53077] = "SetSpellLineRecastTime",
			[53031] = "ChangeRecastTime",
			[53069] = "ChangeLineRecastTime"
		};

		private static void SpellRecast(string rdbDir, string? arg)
		{
			var only = uint.TryParse(arg, out var wanted) ? wanted : 0u;
			using var rdb = OpenRdb(rdbDir);

			var byKind = new SortedDictionary<uint, int>();
			var perSpell = new List<(uint Id, string Name, uint Kind, uint Target, uint Centiseconds)>();
			var scanned = 0;

			foreach (var entry in rdb.EntriesOfType(SpellRecordType))
			{
				if (entry.Size == 0 || (only != 0 && entry.Id != only))
					continue;

				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(entry);
				}
				catch
				{
					continue;
				}

				if (body.Length < 48 || BinaryPrimitives.ReadUInt32LittleEndian(body) != SpellRecordType)
					continue;

				scanned++;
				var (name, _) = RdbFeatImporter.ReadNameAndDescription(body);
				foreach (var element in ItemTemplateStatModifiers.ParseElements(body.AsSpan(12)))
				{
					if (!RecastKinds.ContainsKey(element.Kind))
						continue;

					byKind[element.Kind] = byKind.GetValueOrDefault(element.Kind) + 1;
					// The target slot is the kind's own: the per-spell types key it on KeySpell, the per-line types
					// on KeyLine. Reading it positionally would be right today and wrong the moment a key list grows.
					var target = element.Value(GameActionTypes.KeySpell) ?? element.Value(GameActionTypes.KeyLine) ?? 0;
					perSpell.Add((entry.Id, name, element.Kind, target, element.Value(GameActionTypes.KeyValue) ?? 0));
				}
			}

			Console.WriteLine($"scanned {scanned} type-{SpellRecordType} records; {perSpell.Count} recast elements on {perSpell.Select(x => x.Id).Distinct().Count()} spells");
			Console.WriteLine();
			Console.WriteLine("by KIND:");
			foreach (var (kind, count) in byKind)
				Console.WriteLine($"  {kind} ({RecastKinds[kind]})  x{count}");

			Console.WriteLine();
			Console.WriteLine("spellId\tkind\ttarget\trecast_cs\trecast_s\tname");
			foreach (var row in perSpell.OrderBy(x => x.Id))
				Console.WriteLine($"{row.Id}\t{RecastKinds[row.Kind]}\t{row.Target}\t{row.Centiseconds}\t{row.Centiseconds / 100.0:0.##}\t{row.Name}");
		}
	}
}
