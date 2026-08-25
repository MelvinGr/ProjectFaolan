using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Rdb;
using Faolan.Importer.Rdb;

namespace Faolan.Importer.Cli
{
	// `--report spell-refs --report-arg <spellId>[,<spellId>...]`: every spell record whose GameAction element
	// region NAMES the given spell, with the whole element printed by key.
	//
	// The inverse of `--report spell-template`, which shows what ONE record carries. A buff whose own record is
	// nearly empty - `Root Immunity` (3882140) has five property fields and no elements at all - has its mechanics
	// stated by whatever APPLIES it, and that applier is only findable by scanning the corpus for the id. #659 is
	// the worked case: four spells retail sends a real duration for while their own record's field 8 is 0 or 1 cs.
	//
	// `--report-arg kind:<typeId>[,<typeId>...]` searches by element KIND instead of by referenced value - every
	// spell carrying a `StartSpellWeavingMode` (53190), a `Summon`, a `Teleport`. A kind is the only way to reach a
	// mechanic whose id you do NOT already know, which is the case the value search cannot serve: #141's per-class
	// spellweave profile was found this way, by asking which records declare the weave-mode kind at all rather than
	// by guessing a trance-buff id to look for.
	public static partial class ReportCli
	{
		private static void SpellRefs(string rdbDir, string? arg)
		{
			var raw = (arg ?? "").Trim();
			var byKind = raw.StartsWith("kind:", StringComparison.OrdinalIgnoreCase);
			var wanted = (byKind ? raw["kind:".Length..] : raw)
				.Split(',', StringSplitOptions.RemoveEmptyEntries)
				.Select(s => uint.TryParse(s.Trim(), out var v) ? v : 0u)
				.Where(v => v != 0)
				.ToHashSet();
			if (wanted.Count == 0)
			{
				Console.Error.WriteLine("spell-refs: pass one or more spell ids, e.g. --report-arg 3882140,4784832");
				Console.Error.WriteLine("            or one or more element KINDS, e.g. --report-arg kind:53190");
				return;
			}

			using var rdb = OpenRdb(rdbDir);
			var scanned = 0;
			var hits = new List<(uint Referrer, string Name, uint Target, string Line)>();

			foreach (var entry in rdb.EntriesOfType(SpellRecordType))
			{
				if (entry.Size == 0)
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
					// A text slot's u32 is its BYTE LENGTH, so a short string would collide with a small id. The ids
					// this report is asked about are spell ids (7 digits), well past any text length, and matching on
					// every value slot is what keeps the search independent of which key a kind carries the id in.
					var target = byKind
						? (wanted.Contains(element.Kind) ? element.Kind : 0u)
						: element.Values.FirstOrDefault(wanted.Contains);
					if (target == 0)
						continue;

					var type = element.Type;
					var keys = type == null
						? string.Join(" ", element.Values.Select((v, i) => $"[{i}]={v}"))
						: string.Join(" ", type.Keys.Select((k, i) => $"{k}={(i < element.Values.Length ? element.Values[i].ToString() : "-")}"));
					var texts = string.Join(" ", element.Texts.Where(t => !string.IsNullOrEmpty(t)).Select(t => $"\"{t}\""));
					hits.Add((entry.Id, name, target, $"{element.Kind} {type?.Name ?? "?"}  {keys} {texts}".TrimEnd()));
				}
			}

			Console.WriteLine(
				$"scanned {scanned} type-{SpellRecordType} records for {wanted.Count} {(byKind ? "kind(s)" : "id(s)")}; "
				+ $"{hits.Count} matching elements on {hits.Select(h => h.Referrer).Distinct().Count()} spells");
			foreach (var target in hits.Select(h => h.Target).Distinct().OrderBy(t => t))
			{
				Console.WriteLine();
				Console.WriteLine($"=== {target} ===");
				foreach (var hit in hits.Where(h => h.Target == target).OrderBy(h => h.Referrer))
					Console.WriteLine($"  {hit.Referrer} '{hit.Name}'\n      {hit.Line}");
			}

			foreach (var missing in wanted.Where(w => hits.All(h => h.Target != w)).OrderBy(w => w))
				Console.WriteLine($"\n=== {missing} === (no spell record {(byKind ? "carries this kind" : "references it")})");
		}
	}
}
