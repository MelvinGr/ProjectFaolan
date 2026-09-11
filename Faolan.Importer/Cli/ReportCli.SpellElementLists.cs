using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Rdb;
using Faolan.Importer.Rdb;

namespace Faolan.Importer.Cli
{
	// `--report spell-element-lists`: scores the element-list grammar over the whole spell catalog.
	//
	// A spell record's element region is not one stream. Each group of elements is introduced by a 12-byte header
	// `[2][listId][(count + 1) * 0x3F1]` - the client's ordinary list count word, the same one the wire uses. A spell
	// record that appears to state the same GameAction element twice, verbatim, is stating twins in different lists,
	// which is a fact a reader that streams the region cannot see.
	//
	// This report is the gate on that claim, and it is the only kind that can be: `Faolan.Tests` runs from a plain
	// clone with no RDB, so a whole-catalog assertion has to live where the bytes are (the same reasoning as
	// `UnparsedElementHeads`). It measures three things:
	//
	//   * TILING - does every element of every record fall inside exactly one framed list? A record whose lists do
	//     not account for all its elements is the grammar failing, and it is printed with its id;
	//   * the list ID distribution, by position and by the KINDS each id carries - which is what any future reading
	//     of what the id means has to start from;
	//   * the DUPLICATE elements the ticket is about, split by whether the twins share a list;
	//   * head word `a` cross-tabbed against those lists. `a` had been read as a CATEGORY - "a=2 is an
	//     internal effect record, a=4/5 are castables" - and it is not: it is `2 + element lists + 1 if the record
	//     carries a [22][36] block`, i.e. a count of the record's value blocks, at 97.10%. The category reading was
	//     that count seen through what the records happen to hold. The residual is printed row by row rather
	//     than summarised, because 97% is a reading and a bare percentage would read as a definition.
	public static partial class ReportCli
	{
		private static void SpellElementLists(string rdbDir, string? arg)
		{
			using var rdb = OpenRdb(rdbDir);
			var ldb = LdbStringIndex.EnglishPlaintext(rdb);

			var showKinds = string.Equals((arg ?? "").Trim(), "kinds", StringComparison.OrdinalIgnoreCase);

			long records = 0, withElements = 0, tiled = 0, elements = 0, listed = 0, headerBytes = 0;
			var byId = new Dictionary<uint, int>();
			var kindsById = new Dictionary<uint, Dictionary<string, int>>();
			var listsPerRecord = new Dictionary<int, int>();
			var sameList = 0;
			var crossList = 0;
			var identical = 0;
			var criterionIds = new Dictionary<uint, int>();
			var criterionOps = new Dictionary<uint, int>();
			var zeroIdOps = new Dictionary<uint, int>();
			var untiled = new List<string>();
			var byHeadA = new Dictionary<(uint A, int Lists, bool ListFields), int>();
			var headAAgrees = 0;
			var headAMeasured = 0;

			foreach (var entry in rdb.EntriesOfType(SpellTemplateType).Where(rdb.CanRead))
			{
				byte[] record;
				try
				{
					record = rdb.ReadDecompressed(entry);
				}
				catch
				{
					continue;
				}

				if (record.Length < 12)
					continue;

				records++;
				var body = record.AsSpan(12);
				var all = ItemTemplateStatModifiers.ParseElements(body);

				// Head word `a` against the record's list-valued content. Counted over every record,
				// including the 2,066 that carry no element at all - those are the ones that pin the formula's
				// constant, and skipping them would leave `a`'s floor unmeasured.
				var headA = body.ReadU32Le(HeadWordAOffset);
				var elementLists = ItemTemplateStatModifiers.ParseElementLists(body);
				var listFields = CountListFieldBlocks(body) > 0;
				if (all.Count == 0 || elementLists.Sum(l => l.Elements.Count) == all.Count)
				{
					headAMeasured++;
					byHeadA[(headA, elementLists.Count, listFields)] = byHeadA.GetValueOrDefault((headA, elementLists.Count, listFields)) + 1;
					if (headA == HeadWordAFloor + elementLists.Count + (listFields ? 1 : 0))
						headAAgrees++;
				}

				if (all.Count == 0)
					continue;

				withElements++;
				elements += all.Count;

				var lists = elementLists;
				var covered = lists.Sum(l => l.Elements.Count);
				listed += covered;

				if (covered != all.Count)
				{
					if (untiled.Count < 12)
						untiled.Add($"{ldb.GetValueOrDefault((LdbStringIndex.SpellNameCategory, entry.Id), "?")} ({entry.Id}): "
						            + $"{covered} of {all.Count} elements in {lists.Count} list(s)");

					continue;
				}

				tiled++;
				headerBytes += lists.Count * 12;
				listsPerRecord[lists.Count] = listsPerRecord.GetValueOrDefault(lists.Count) + 1;

				var seen = new Dictionary<string, (int List, string Whole)>();
				for (var i = 0; i < lists.Count; i++)
				{
					var list = lists[i];
					byId[list.Id] = byId.GetValueOrDefault(list.Id) + 1;
					foreach (var element in list.Elements)
					{
						var name = GameActionTypes.Get(element.Kind)?.Name ?? element.Kind.ToString();
						if (showKinds)
						{
							var bucket = kindsById.TryGetValue(list.Id, out var k) ? k : kindsById[list.Id] = [];
							bucket[name] = bucket.GetValueOrDefault(name) + 1;
						}

						// An element is "the same" when its kind and every decoded value match - the ticket's
						// "verbatim". Comparing the decoded slots rather than the raw bytes is deliberate: the bytes
						// carry the sub-record run and the fixed words, and two elements can differ there and still
						// state the same effect, which is the thing that reads as a duplicate.
						foreach (var criterion in element.Criteria)
						{
							criterionIds[criterion.Id] = criterionIds.GetValueOrDefault(criterion.Id) + 1;
							criterionOps[criterion.Operator] = criterionOps.GetValueOrDefault(criterion.Operator) + 1;
							if (criterion is { Id: 0, Value: 0 })
								zeroIdOps[criterion.Operator] = zeroIdOps.GetValueOrDefault(criterion.Operator) + 1;
						}

						var key = $"{element.Kind}|{string.Join(",", element.Values)}|{element.Fixed2d5}|{element.Fixed2d6}";
						var whole = key + "|" + string.Join(",", element.SubRecords);
						if (seen.TryGetValue(key, out var first))
						{
							if (first.List == i)
								sameList++;
							else
								crossList++;

							if (first.Whole == whole)
								identical++;
						}
						else
						{
							seen[key] = (i, whole);
						}
					}
				}
			}

			Console.WriteLine($"spell element lists over {records:N0} records of RDB {SpellTemplateType} (#785)");
			Console.WriteLine($"  records with at least one element : {withElements:N0}");
			Console.WriteLine($"  records whose lists TILE exactly  : {tiled:N0} / {withElements:N0}"
			                  + $" = {(withElements == 0 ? 0 : 100.0 * tiled / withElements):0.00}%");
			Console.WriteLine($"  elements inside a framed list     : {listed:N0} / {elements:N0}"
			                  + $" = {(elements == 0 ? 0 : 100.0 * listed / elements):0.00}%");
			Console.WriteLine($"  list-header bytes accounted for   : {headerBytes:N0}");
			Console.WriteLine();

			Console.WriteLine("  lists per record:");
			foreach (var (count, n) in listsPerRecord.OrderBy(k => k.Key))
				Console.WriteLine($"    {count} list(s)   {n,7:N0} record(s)");

			Console.WriteLine();
			Console.WriteLine($"  ⭐ head word `a` vs the record's LIST-valued fields, over {headAMeasured:N0} records (#632):");
			Console.WriteLine($"     `a` == {HeadWordAFloor} + element lists + 1 if the record carries a [22][36] block"
			                  + $"   : {headAAgrees:N0} / {headAMeasured:N0}"
			                  + $" = {(headAMeasured == 0 ? 0 : 100.0 * headAAgrees / headAMeasured):0.00}%");
			Console.WriteLine($"     {"a",3} {"lists",6} {"[22][36]",9} {"records",9}   {"a - predicted",13}");
			foreach (var ((a, lists, fields), n) in byHeadA.OrderBy(k => k.Key.A).ThenBy(k => k.Key.Lists).ThenBy(k => k.Key.ListFields))
			{
				var delta = (long)a - (HeadWordAFloor + lists + (fields ? 1 : 0));
				Console.WriteLine($"     {a,3} {lists,6} {(fields ? "yes" : "no"),9} {n,9:N0}   {(delta == 0 ? "" : delta.ToString("+#;-#")),13}");
			}

			Console.WriteLine();
			Console.WriteLine("  list ids, by how many lists carry one:");
			foreach (var (id, n) in byId.OrderByDescending(k => k.Value))
				Console.WriteLine($"    id {id,4}   {n,7:N0}");

			if (showKinds)
			{
				Console.WriteLine();
				Console.WriteLine("  the kinds each id carries (--report-arg kinds):");
				foreach (var (id, kinds) in kindsById.OrderByDescending(k => k.Value.Values.Sum()))
				{
					Console.WriteLine($"    id {id}  ({kinds.Values.Sum():N0} elements)");
					foreach (var (name, n) in kinds.OrderByDescending(k => k.Value).Take(10))
						Console.WriteLine($"        {name,-40} {n,6:N0}");
				}
			}

			Console.WriteLine();
			Console.WriteLine($"  elements whose DECODED SLOTS repeat another of the same record: {sameList + crossList:N0}"
			                  + $"   ({crossList:N0} across two lists, {sameList:N0} inside one)");
			// The number that matters. An element carries a sub-record run the decoded slots do not show, and most of
			// the apparent duplicates differ there - so they are not repeated data, they are elements this reader
			// cannot yet tell apart.
			Console.WriteLine($"    ...of which byte-identical incl. their CRITERIA: {identical:N0}"
			                  + $"   ({sameList + crossList - identical:N0} are the same effect under different conditions)");

			Console.WriteLine();
			Console.WriteLine($"  criteria ([id][value][operator], named by FUN_006e9f20's own assert): "
			                  + $"{criterionIds.Values.Sum():N0} over {criterionIds.Count} ids and {criterionOps.Count} operators");
			Console.WriteLine("    top ids:       " + string.Join(", ", criterionIds.OrderByDescending(k => k.Value).Take(10).Select(k => $"{k.Key}x{k.Value:N0}")));
			Console.WriteLine("    top operators: " + string.Join(", ", criterionOps.OrderByDescending(k => k.Value).Take(10).Select(k => $"{k.Key}x{k.Value:N0}")));
			// A criterion with both id and value zero cannot be a comparison, so its operator is a logical combiner
			// - which is what a postfix (RPN) requirement expression looks like. Reported separately because the
			// split is the evidence for that reading rather than an assertion of it.
			Console.WriteLine($"    of those, [0][0][op] (no operand - a logical combiner?): {zeroIdOps.Values.Sum():N0}"
			                  + "   ops: " + string.Join(", ", zeroIdOps.OrderByDescending(k => k.Value).Take(8).Select(k => $"{k.Key}x{k.Value:N0}")));

			// Is a criterion id a stat id? Asked of the data rather than argued: every id is
			// printed against the client's own stat-name table (`CharacterStatId`, `docs/re/aoc-client-stat-names.tsv`).
			// The hit rate is not the evidence - the OPERATORS are printed through the same table as the control
			// and all 50 of those "resolve" too, because the table is dense at low ids. What separates them is
			// COHERENCE: the ids read as a condition vocabulary end to end (`Level`, `Race`, `CombatMode`,
			// `CurrentPlayfield`, `Rnd`) while the control comes back `RespecPoints` / `VolumeMass` / `CATMesh`.
			Console.WriteLine();
			Console.WriteLine($"    every criterion ID against the client's stat-name table ({criterionIds.Count} distinct):");
			foreach (var (id, n) in criterionIds.OrderByDescending(k => k.Value))
				Console.WriteLine($"      {id,6} x{n,-7:N0} {ClientStatName(id)}");

			// The operator read through its own vocabulary - `CriteriaItem_t::CheckCriteria`'s switch,
			// `docs/re/criteria-grammar.md` - beside the stat-table reading it is not.
			Console.WriteLine();
			Console.WriteLine($"    every OPERATOR ({criterionOps.Count} distinct):  value, its name, then the CONTROL - what the stat table would have called it");
			foreach (var (op, n) in criterionOps.OrderByDescending(k => k.Value))
			{
				var name = Enum.IsDefined(typeof(CriteriaOperator), op) ? ((CriteriaOperator)op).ToString() : "";
				Console.WriteLine($"      {op,6} x{n,-7:N0} {name,-30} (control: {ClientStatName(op)})");
			}

			var opsNamed = criterionOps.Keys.Count(k => Enum.IsDefined(typeof(CriteriaOperator), k));
			var sightingsNamed = criterionOps.Where(k => Enum.IsDefined(typeof(CriteriaOperator), k.Key)).Sum(k => k.Value);
			Console.WriteLine();
			Console.WriteLine($"    operators the client NAMES: {opsNamed} / {criterionOps.Count}"
			                  + $"   covering {sightingsNamed:N0} / {criterionOps.Values.Sum():N0} sightings"
			                  + $" = {(criterionOps.Count == 0 ? 0 : 100.0 * sightingsNamed / criterionOps.Values.Sum()):0.0}%");
			Console.WriteLine($"    the stat table would name {criterionIds.Keys.Count(k => ClientStatName(k).Length > 0)} / {criterionIds.Count} ids"
			                  + $" and {criterionOps.Keys.Count(k => ClientStatName(k).Length > 0)} / {criterionOps.Count} operators - which is why the rate alone proves nothing");

			if (untiled.Count != 0)
			{
				Console.WriteLine();
				Console.WriteLine("  ⚠️ records whose lists do NOT account for every element:");
				foreach (var line in untiled)
					Console.WriteLine($"    {line}");
			}
		}

		// How many `[22][36]` list-valued field blocks does the record carry? Usually the block sits immediately
		// after the string block, but not always - on some records it turns up between two element lists, and a
		// couple carry it twice - so this SCANS from the end of the text rather than reading one fixed spot.
		// `RdbSpellImporter` owns the shape.
		private static int CountListFieldBlocks(ReadOnlySpan<byte> body)
		{
			RdbSpellImporter.ReadIncreasingFields(body, out var afterRun);
			if (afterRun <= 0)
				return 0;

			var (_, _, stringBytes) = RdbSpellImporter.ReadStringBlock(body, afterRun);
			if (stringBytes == 0)
				return 0;

			var blocks = 0;
			for (var at = afterRun + stringBytes; at + 8 <= body.Length;)
			{
				var length = RdbSpellImporter.ReadListFieldsLength(body, at);
				if (length > 0)
				{
					blocks++;
					at += length;
					continue;
				}

				at += 4;
			}

			return blocks;
		}

		// The client's own name for a stat id, or "" when the table does not name it.
		private static string ClientStatName(uint id)
		{
			return Enum.IsDefined(typeof(CharacterStatId), id) ? ((CharacterStatId)id).ToString() : "";
		}

		// The record opens `[classTag][a][15][23][frame]`, so `a` is word 1 of the body after the 12-byte RDB header.
		private const int HeadWordAOffset = 4;

		// `a`'s floor: the value a record with no list-valued field at all carries.
		private const uint HeadWordAFloor = 2;
	}
}
