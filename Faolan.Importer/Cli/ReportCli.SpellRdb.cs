using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Cli;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Rdb;
using Faolan.GameServer.Combat;
using Faolan.Importer.Captures;
using Faolan.Importer.Rdb;

namespace Faolan.Importer.Cli
{
	public static partial class ReportCli
	{
		// Property-table field 128: the spell's LINE id, shared by all its ranks.
		private const uint SpellLineField = 128;

		// Property-table field 132: the mana cost, in whichever of its two modes the (undecoded) flag selects - see
		// SpellManaMode and RdbSpellImporter.ManaCostField.
		private const uint SpellManaCostField = 132;

		// Byte 52 of a spell record - word 13, the FLAGS word, and the one the client keeps at `spell + 0xC8` (#630).
		// Its bit 2 is what picks a percent-of-pool mana cost over a flat one.
		private const int SpellFlagsWordOffset = 52;

		// --report spell-line-ids [--report-arg <lineId>]
		//
		// The SPELL-LINE id every spell template carries in property-table field 128 (#590). It is what a feat's
		// modifier record references: `Decomposition` points at 41000 and `Flesh to Worms` carries 41000, so a feat's
		// "+35% on Flesh to Worms" resolves through this and nothing else.
		//
		// The id is `ConanClass x 1000 + lineIndex` - Necromancer (41) owns 41000 Flesh to Worms, 41001 Pestilential
		// Blast, 41005 Ice Strike - which is why the same line id is shared by every RANK of a spell, and why a feat
		// can name a spell without naming a rank.
		//
		// --report spell-mana-mode [--report-arg <threshold>]   (default threshold 100)
		//
		// #630: the client reads spell field 132 (the mana cost) in TWO modes - a percentage of the caster's mana
		// pool, or an absolute cost in wire hundredths - and picks between them with bit 2 of a flag word on the
		// loaded spell object (`*(uint*)(spell + 0xC8) >> 2 & 1`, FUN_008d6f70). What FILLS that word is unknown, so
		// `CombatResolver.ResolveSpellManaCost` picks the mode by MAGNITUDE instead: <= 100 is a percent.
		//
		// This asks the catalog whether any OTHER field determines the same split. For every field F the record
		// carries, it buckets the records by F's value and measures how PURE each bucket is with respect to the
		// percent/flat class. A field that IS the flag (or that carries it) partitions perfectly: every one of its
		// values sees only percents or only flats. Ranked by purity, so a partial signal is visible too.
		//
		// ⚠️ The class comes from the heuristic being tested, so this cannot CONFIRM the heuristic - only find a field
		// that agrees with it. A perfect separator would be strong evidence; the absence of one says the flag is not
		// in the ascending property run at all, which is itself worth knowing (it would point at the record's fixed
		// head, or at data outside the record).
		private static void SpellManaMode(string rdbDir, string? arg)
		{
			var threshold = uint.TryParse(arg, out var t) && t > 0 ? t : 100u;
			using var rdb = OpenRdb(rdbDir);

			// perClass[field][value] = (percents, flats) among the records carrying that field at that value.
			var perField = new Dictionary<uint, Dictionary<uint, (int Percent, int Flat)>>();
			var headWords = new Dictionary<int, Dictionary<uint, (int Percent, int Flat)>>();
			var percents = 0;
			var flats = 0;

			foreach (var e in rdb.EntriesOfType(SpellTemplateType))
			{
				if (e.Size == 0)
					continue;

				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue;
				}

				if (body.Length < 48 || BinaryPrimitives.ReadUInt32LittleEndian(body) != SpellTemplateType)
					continue;

				var fields = RdbSpellImporter.ReadIncreasingFields(body.AsSpan(12));
				if (!fields.TryGetValue(SpellManaCostField, out var cost) || cost == 0)
					continue; // a free spell states no mode

				var isPercent = cost <= threshold;
				if (isPercent)
					percents++;
				else
					flats++;

				// The ascending run is only ~16 fields wide and starts around word 15 of the body, so the great
				// majority of a 964-byte record is outside it. Sweep the FIXED head at word granularity too - a flag
				// word that is part of the record's struct rather than its property table would live there.
				for (var w = 0; w < HeadWords && (w + 1) * 4 <= body.Length; w++)
				{
					var word = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(w * 4, 4));
					var byValue = headWords.TryGetValue(w, out var hv) ? hv : headWords[w] = new Dictionary<uint, (int, int)>();
					var (hp, hf) = byValue.GetValueOrDefault(word);
					byValue[word] = isPercent ? (hp + 1, hf) : (hp, hf + 1);
				}

				foreach (var (field, value) in fields)
				{
					if (field == SpellManaCostField)
						continue;

					var byValue = perField.TryGetValue(field, out var bv) ? bv : perField[field] = new Dictionary<uint, (int, int)>();
					var (p, f) = byValue.GetValueOrDefault(value);
					byValue[value] = isPercent ? (p + 1, f) : (p, f + 1);
				}
			}

			Console.WriteLine($"Spell mana-cost mode split at field {SpellManaCostField} <= {threshold}: {percents} percent, {flats} flat "
			                  + $"({percents + flats} records state a cost).");
			Console.WriteLine("A field that determines the mode separates them perfectly - every value of it seeing only one class.\n");
			Console.WriteLine($"{"field",6} {"records",8} {"values",7} {"purity",8}  {"mixed values",12}");

			var ranked = perField
				.Select(kv =>
				{
					var covered = kv.Value.Sum(v => v.Value.Percent + v.Value.Flat);
					var majority = kv.Value.Sum(v => Math.Max(v.Value.Percent, v.Value.Flat));
					var mixed = kv.Value.Count(v => v.Value is { Percent: > 0, Flat: > 0 });
					return (Field: kv.Key, Covered: covered, Values: kv.Value.Count, Purity: (double)majority / Math.Max(covered, 1), Mixed: mixed);
				})
				// A field on a handful of records separates them trivially and says nothing, so rank by purity but
				// require it to cover a real share of the catalog before it counts as an answer.
				.OrderByDescending(x => x.Purity)
				.ThenByDescending(x => x.Covered)
				.ToList();

			foreach (var x in ranked.Take(25))
				Console.WriteLine($"{x.Field,6} {x.Covered,8} {x.Values,7} {x.Purity,8:P2}  {x.Mixed,12}");

			var separators = ranked.Where(x => x.Mixed == 0 && x.Covered >= (percents + flats) / 2).ToList();
			Console.WriteLine(separators.Count == 0
				? "\nNo field covering at least half the catalog separates the two modes cleanly."
				: $"\nPERFECT SEPARATORS covering >= half the catalog: {string.Join(", ", separators.Select(s => s.Field))}");

			// The client does not read a MODE field - it reads one BIT of a flag word (`spell + 0xC8 >> 2 & 1`). So
			// the value-bucket sweep above is the wrong shape if the record stores that word packed: a bitmask field
			// takes hundreds of values, each seen a handful of times, and looks like noise. Test the bits directly.
			//
			// A field ABSENT from a record is not a zero bit - it is no statement at all - so the absent records are
			// counted separately rather than folded into the 0 side, where they would manufacture agreement.
			Console.WriteLine("\nBit sweep - is some single BIT of some field the selector?\n");
			Console.WriteLine($"{"field",6} {"bit",4} {"records",8} {"agree",8}  set:pct/flat  clear:pct/flat");

			var bits = new List<(uint Field, int Bit, int Covered, double Agreement, int SetP, int SetF, int ClearP, int ClearF)>();
			foreach (var (field, byValue) in perField)
			{
				for (var bit = 0; bit < 32; bit++)
				{
					int setP = 0, setF = 0, clearP = 0, clearF = 0;
					foreach (var (value, (p, f)) in byValue)
					{
						if ((value >> bit & 1) == 1)
						{
							setP += p;
							setF += f;
						}
						else
						{
							clearP += p;
							clearF += f;
						}
					}

					var covered = setP + setF + clearP + clearF;
					if (setP + setF == 0 || clearP + clearF == 0)
						continue; // the bit never varies over the records carrying this field - it states nothing

					// Either polarity is a candidate: the bit may mean percent or mean flat.
					var agreement = Math.Max(setP + clearF, setF + clearP) / (double)covered;
					bits.Add((field, bit, covered, agreement, setP, setF, clearP, clearF));
				}
			}

			foreach (var b in bits.OrderByDescending(x => x.Agreement).ThenByDescending(x => x.Covered).Take(15))
				Console.WriteLine($"{b.Field,6} {b.Bit,4} {b.Covered,8} {b.Agreement,8:P2}  {b.SetP,5}/{b.SetF,-5} {b.ClearP,7}/{b.ClearF,-5}");

			var perfectBits = bits.Where(b => b.Agreement >= 1.0 && b.Covered >= (percents + flats) / 2).ToList();
			Console.WriteLine(perfectBits.Count == 0
				? "\nNo single bit of any field covering at least half the catalog selects the mode."
				: $"\nSELECTOR BITS: {string.Join(", ", perfectBits.Select(b => $"field {b.Field} bit {b.Bit}"))}");

			// Third shape: the flag may not be a VALUE at all. A field that is written only when the spell is in one
			// mode makes its own PRESENCE the statement, and both sweeps above are blind to that - they only ever look
			// at the records carrying the field, where a perfectly one-sided field looks pure but tiny.
			Console.WriteLine("\nPresence sweep - is a field's mere PRESENCE the statement?\n");
			Console.WriteLine($"{"field",6} {"on pct",8} {"of",6} {"on flat",8} {"of",6}  {"one-sided coverage",18}");

			foreach (var x in perField
				         .Select(kv => (Field: kv.Key, P: kv.Value.Sum(v => v.Value.Percent), F: kv.Value.Sum(v => v.Value.Flat)))
				         // How much of ONE class it covers while staying out of the other - 1.0 would be the flag.
				         .Select(x => (x.Field, x.P, x.F, Score: Math.Max(x.P == 0 ? 0 : (double)x.F / flats * (x.P == 0 ? 1 : 0), x.F == 0 ? (double)x.P / percents : 0)))
				         .OrderByDescending(x => x.Score)
				         .ThenByDescending(x => x.P + x.F)
				         .Take(12))
				Console.WriteLine($"{x.Field,6} {x.P,8} {percents,6} {x.F,8} {flats,6}  {x.Score,18:P2}");

			// Fourth shape: the record's fixed HEAD, at word granularity, bit by bit. Same test as the field bit
			// sweep, over the region the property reader never reaches.
			Console.WriteLine($"\nHead-word bit sweep - the first {HeadWords} words of the record body:\n");
			Console.WriteLine($"{"word",5} {"@byte",6} {"bit",4} {"agree",8}  set:pct/flat  clear:pct/flat");

			var headBits = new List<(int Word, int Bit, double Agreement, int SetP, int SetF, int ClearP, int ClearF)>();
			foreach (var (word, byValue) in headWords)
			{
				for (var bit = 0; bit < 32; bit++)
				{
					int setP = 0, setF = 0, clearP = 0, clearF = 0;
					foreach (var (value, (p, f)) in byValue)
					{
						if ((value >> bit & 1) == 1)
						{
							setP += p;
							setF += f;
						}
						else
						{
							clearP += p;
							clearF += f;
						}
					}

					if (setP + setF == 0 || clearP + clearF == 0)
						continue;

					headBits.Add((word, bit, Math.Max(setP + clearF, setF + clearP) / (double)(percents + flats), setP, setF, clearP, clearF));
				}
			}

			foreach (var b in headBits.OrderByDescending(x => x.Agreement).Take(15))
				Console.WriteLine($"{b.Word,5} {b.Word * 4,6} {b.Bit,4} {b.Agreement,8:P2}  {b.SetP,5}/{b.SetF,-5} {b.ClearP,7}/{b.ClearF,-5}");

			// The answer is word 13 bit 2 (see SpellFlagsWordOffset). Where it and the magnitude heuristic DISAGREE is
			// the only interesting set left, because one of them is wrong there - so list it rather than summarising
			// it away. Every disagreement is the same direction: the heuristic says percent, the flag says flat.
			Console.WriteLine($"\nWhere the flag (word {SpellFlagsWordOffset / 4} bit 2) and the <= {threshold} heuristic disagree:\n");
			var disagreements = new List<(uint Id, string Name, uint Cost)>();
			foreach (var e in rdb.EntriesOfType(SpellTemplateType))
			{
				if (e.Size == 0)
					continue;

				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue;
				}

				if (body.Length < SpellFlagsWordOffset + 4 || BinaryPrimitives.ReadUInt32LittleEndian(body) != SpellTemplateType)
					continue;

				var cost = RdbSpellImporter.ReadIncreasingFields(body.AsSpan(12)).GetValueOrDefault(SpellManaCostField);
				if (cost == 0)
					continue;

				var flagSaysPercent = (BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(SpellFlagsWordOffset, 4)) >> 2 & 1) == 1;
				if (flagSaysPercent == cost <= threshold)
					continue;

				var (name, _) = RdbFeatImporter.ReadNameAndDescription(body);
				disagreements.Add((e.Id, name, cost));
			}

			Console.WriteLine($"{disagreements.Count} of {percents + flats} records disagree; costs {string.Join(", ", disagreements.Select(d => d.Cost).Distinct().Order().Take(20))}");
			foreach (var d in disagreements.OrderBy(d => d.Cost).Take(15))
				Console.WriteLine($"  {d.Id,9} {d.Name,-44} {d.Cost}");
		}

		// ⚠️ A spell template's property table ASCENDS, where an item's strictly DESCENDS (RdbItemImporter's
		// TryReadPropertyField). Same [fieldId][value] grammar, opposite direction - reusing the item reader here
		// finds nothing at all.
		private static void SpellLineIds(string rdbDir, string? arg)
		{
			var wanted = uint.TryParse(arg, out var w) ? w : 0u;
			using var rdb = OpenRdb(rdbDir);
			var byLine = new SortedDictionary<uint, List<string>>();
			var scanned = 0;
			var carrying = 0;

			foreach (var e in rdb.EntriesOfType(SpellTemplateType))
			{
				if (e.Size == 0)
					continue;

				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue;
				}

				if (body.Length < 48 || BinaryPrimitives.ReadUInt32LittleEndian(body) != SpellTemplateType)
					continue;

				scanned++;
				var line = RdbSpellImporter.ReadIncreasingFields(body.AsSpan(12)).GetValueOrDefault(SpellLineField);
				if (line == 0)
					continue;

				carrying++;
				if (wanted != 0 && line != wanted)
					continue;

				var (name, _) = RdbFeatImporter.ReadNameAndDescription(body);
				byLine.TryAdd(line, []);
				byLine[line].Add($"{e.Id} {name}");
			}

			Console.WriteLine($"spell templates scanned {scanned}, carrying field {SpellLineField}: {carrying}");
			Console.WriteLine();
			foreach (var (line, names) in byLine)
			{
				var @class = line / 1000;
				var known = Enum.IsDefined(typeof(ConanClass), (ConanClass)@class) ? ((ConanClass)@class).ToString() : $"class {@class}?";
				Console.WriteLine($"  {line}  ({known}, line {line % 1000})  x{names.Count}");
				foreach (var n in names.Take(wanted != 0 ? 40 : 3))
					Console.WriteLine($"      {n}");
			}
		}

		// The spell records' (type 1040005) property table, three ways:
		//   --report-arg <decimal id>     one spell's whole field table
		//   --report-arg field:<n>        every spell CARRYING field n, with its name and the value - the view that
		//                                 says whether an unread field is worth importing (#633)
		//   (no arg)                      a histogram of how often each field id appears, with samples
		//
		// Written for #274 (find the DURATION field) and kept because every later field decode has needed one of the
		// three - a field is only decodable once you can see WHICH spells carry it, which the histogram cannot say.
		private static void SpellFields(string rdbDir, string? arg)
		{
			using var rdb = OpenRdb(rdbDir);
			var counts = new Dictionary<uint, int>();
			var samples = new Dictionary<uint, List<uint>>();
			uint? byField = arg != null && arg.StartsWith("field:", StringComparison.OrdinalIgnoreCase) && uint.TryParse(arg[6..], out var f)
				? f
				: null;
			var carriers = new List<(uint Id, string Name, uint Value)>();
			uint? wanted = uint.TryParse(arg, out var w)
				? w
				: null;
			foreach (var e in rdb.EntriesOfType(1040005))
			{
				if (e.Size == 0 || !rdb.CanRead(e))
					continue;

				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue;
				}

				if (body.Length < 12)
					continue;

				var name = RdbItemImporter.ExtractName(body);
				if (string.IsNullOrEmpty(name))
					continue;

				var fields = ReadIncreasingFields(body.AsSpan(12));
				if (wanted is { } id && e.Id == id)
				{
					Console.WriteLine($"spell {e.Id} '{name}':");
					foreach (var kv in fields.OrderBy(k => k.Key))
						Console.WriteLine($"  0x{kv.Key:X3} ({kv.Key,4}) = {kv.Value}  (0x{kv.Value:X})");
					return;
				}

				if (byField is { } want && fields.TryGetValue(want, out var value))
					carriers.Add((e.Id, name, value));

				foreach (var kv in fields)
				{
					counts[kv.Key] = counts.GetValueOrDefault(kv.Key) + 1;
					if (!samples.TryGetValue(kv.Key, out var l))
						samples[kv.Key] = l = [];
					if (l.Count < 8)
						l.Add(kv.Value);
				}
			}

			if (byField is { } field)
			{
				var fieldName = Enum.GetName(typeof(CharacterStatId), field) ?? "(unnamed)";
				Console.WriteLine($"field {field} (0x{field:X3}) {fieldName}: {carriers.Count} spell record(s)");
				foreach (var c in carriers.OrderBy(c => c.Value).ThenBy(c => c.Name, StringComparer.Ordinal))
					Console.WriteLine($"  {c.Value,10}  {c.Id,9}  {c.Name}");

				foreach (var g in carriers.GroupBy(c => c.Value).OrderByDescending(g => g.Count()).Take(15))
					Console.WriteLine($"  value {g.Key,10} x{g.Count()}");

				return;
			}

			if (wanted != null)
			{
				Console.WriteLine($"spell {wanted} not found / unnamed");
				return;
			}

			// Every field id, not a top-N: the tail is where the undecoded ones live. The name comes from the
			// client's own static stat-name table (CharacterStatId, `docs/re/aoc-client-stat-names.tsv`) - the
			// spell record's field ids ARE that namespace, which is what names the 0x300+ run outright.
			foreach (var kv in counts.OrderByDescending(k => k.Value))
			{
				var name = Enum.GetName(typeof(CharacterStatId), kv.Key) ?? "";
				Console.WriteLine($"0x{kv.Key:X3} ({kv.Key,4}) count={kv.Value,5}  {name,-38} samples={string.Join(",", samples[kv.Key])}");
			}
		}
	}
}
