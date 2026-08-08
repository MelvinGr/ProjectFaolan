using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report buff-descriptor [--report-arg tsv]`
	//
	// The evidence view behind #658: every captured `MagicBuffIIR` APPLY's `descriptor[0]` @14 and `bool2` @13,
	// grouped by SPELL and cross-tabbed against the `Spells` columns we hold. `MagicBuffCorpusTests` measured both as
	// *nearly a property of the spell* (varying within a spell on 7 and 3 of 256 spells respectively), which is the
	// shape that says the value comes off the spell's own record rather than from per-cast state - so this prints the
	// per-spell table plus, for every candidate column, how well it PARTITIONS the two values.
	//
	// The purity number is the one to read: for a candidate column, group the spells by that column's value and score
	// the share that land in each group's majority `descriptor[0]`. A column that predicts the field scores 100%; the
	// baseline is whatever the single most common value scores on its own, and a candidate that does not beat the
	// baseline is not an answer however plausible its name.
	//
	// `--report-arg tsv` dumps the per-spell rows instead, for ad-hoc cross-tabs against sources the report does not
	// join (the RDB record's unread head, #632).
	public static partial class ReportCli
	{
		private static void BuffDescriptor(IReadOnlyList<NamedCapture> captures, string? mode)
		{
			if (string.Equals(mode, "rdb", StringComparison.OrdinalIgnoreCase))
			{
				BuffDescriptorRdbSweep(captures, RdbDir());
				return;
			}

			var applies = new List<(uint SpellId, uint Descriptor0, byte Bool2)>();
			var crossTab = new Dictionary<string, int>();
			var nonZeroSlots = new List<string>();
			var descriptor4 = new Dictionary<uint, int>();
			var descriptor4Spells = new Dictionary<uint, int>();
			foreach (var capture in captures)
			foreach (var p in capture.Packets)
			{
				if (p is not { IsIirCarrier: true, SubOpcode: MagicBuffCodec.Opcode })
					continue;

				if (!p.TryParse(MagicBuffCodec.Parse, out var buff) || buff.Form != MagicBuffIds.FormApply || buff.Data.Length != MagicBuffDataCodec.Length)
					continue;

				var payload = MagicBuffDataCodec.Parse(buff.Data);
				applies.Add((buff.SpellBaseId, payload.Descriptor[0], payload.Bool2));

				// The within-payload cross-tab. `descriptor` is seven words and only [2] (the spell id) and [3] (the
				// caster's level) are named, so the first question to ask of [0] is whether it counts or gates one of
				// the others - [1] and [4] are the two that are near-zero rather than zero.
				var key = $"d0={payload.Descriptor[0]} bool2={payload.Bool2} d1!=0:{payload.Descriptor[1] != 0} d4!=0:{payload.Descriptor[4] != 0} self:{buff.ApplyFrom == buff.ApplyTo}";
				crossTab[key] = crossTab.GetValueOrDefault(key) + 1;

				if (payload.Descriptor[1] != 0)
					nonZeroSlots.Add($"descriptor[1]={payload.Descriptor[1]} on 0x{buff.SpellBaseId:X8} d0={payload.Descriptor[0]} from=0x{buff.ApplyFrom:X8} to=0x{buff.ApplyTo:X8}");

				if (payload.Descriptor[4] != 0)
				{
					descriptor4[payload.Descriptor[4]] = descriptor4.GetValueOrDefault(payload.Descriptor[4]) + 1;
					descriptor4Spells[buff.SpellBaseId] = descriptor4Spells.GetValueOrDefault(buff.SpellBaseId) + 1;
				}
			}

			if (applies.Count == 0)
			{
				Console.WriteLine("no MagicBuff applies in the corpus.");
				return;
			}

			using var db = OpenDb();
			var ids = applies.Select(a => a.SpellId).Distinct().ToList();
			var spells = db.Spells.Where(s => ids.Contains(s.Id)).ToList().ToDictionary(s => s.Id);

			// Every spell's modal value of each field, plus whether the spell is split (the exceptions the per-spell
			// reading has to survive).
			var perSpell = applies
				.GroupBy(a => a.SpellId)
				.Select(g => new SpellDescriptor(
					g.Key,
					spells.TryGetValue(g.Key, out var row) ? row.Name : $"(uncatalogued 0x{g.Key:X8})",
					g.Count(),
					Modal(g.Select(a => a.Descriptor0)),
					g.Select(a => a.Descriptor0).Distinct().Count() > 1,
					Modal(g.Select(a => (uint)a.Bool2)),
					g.Select(a => (uint)a.Bool2).Distinct().Count() > 1))
				.OrderBy(s => s.Name)
				.ToList();

			if (string.Equals(mode, "tsv", StringComparison.OrdinalIgnoreCase))
			{
				Console.WriteLine("spellId\tname\tapplies\tdescriptor0\td0Split\tbool2\tb2Split\tdurationMs\tcastTime\trecastCs\tdotType\tdotDelta\teffectSpell\tmanaCost\tmanaPct\tscalingAttr\tstackLine\tstackOrder\tlevel\trange\tareaRadius\tchannelMs");
				foreach (var s in perSpell)
				{
					var r = spells.GetValueOrDefault(s.SpellId);
					Console.WriteLine($"{s.SpellId}\t{s.Name}\t{s.Applies}\t{s.Descriptor0}\t{s.Descriptor0Split}\t{s.Bool2}\t{s.Bool2Split}\t"
					                  + $"{r?.DurationMs}\t{r?.CastTime}\t{r?.RecastCentiseconds}\t{r?.DotType}\t{r?.DotDelta}\t{r?.EffectSpell}\t{r?.ManaCost}\t"
					                  + $"{r?.ManaCostIsPercent}\t{r?.ScalingAttribute}\t{r?.GuiStackingLine}\t{r?.GuiStackingOrder}\t{r?.Level}\t{r?.Range}\t{r?.AreaRadius}\t{r?.ChannelingIntervalMs}");
				}

				return;
			}

			Console.WriteLine($"{applies.Count} captured applies over {perSpell.Count} spells.");
			Console.WriteLine($"  descriptor[0]: {Histogram(applies.Select(a => a.Descriptor0))}   split within a spell on {perSpell.Count(s => s.Descriptor0Split)}");
			Console.WriteLine($"  bool2        : {Histogram(applies.Select(a => (uint)a.Bool2))}   split within a spell on {perSpell.Count(s => s.Bool2Split)}");
			Console.WriteLine($"  catalogued   : {perSpell.Count(s => spells.ContainsKey(s.SpellId))} of {perSpell.Count} spells have a Spells row");
			Console.WriteLine();
			Console.WriteLine("within-payload cross-tab (does descriptor[0] count or gate another word?):");
			foreach (var (key, count) in crossTab.OrderByDescending(kv => kv.Value))
				Console.WriteLine($"  {count,5}  {key}");
			Console.WriteLine();
			Console.WriteLine("the two near-zero slots, in full:");
			foreach (var line in nonZeroSlots)
				Console.WriteLine($"  {line}");
			Console.WriteLine($"  descriptor[4] non-zero values: {string.Join(" ", descriptor4.OrderByDescending(kv => kv.Value).Take(20).Select(kv => $"{kv.Key}x{kv.Value}"))}");
			Console.WriteLine($"  descriptor[4] set on {descriptor4Spells.Count} spells: {string.Join(" ", descriptor4Spells.OrderByDescending(kv => kv.Value).Take(20).Select(kv => $"0x{kv.Key:X8}x{kv.Value}"))}");
			Console.WriteLine();

			// The candidate columns, each as a coarse KEY over the spell. Deliberately coarse: a key with as many
			// values as there are spells scores 100% for free and says nothing (the `field-predictors` report's
			// "(fine)" trap).
			var candidates = new (string Name, Func<Core.Database.Tables.Spell, string> Key)[]
			{
				("DurationMs == 0", s => (s.DurationMs == 0).ToString()),
				("DotType", s => s.DotType.ToString()),
				("DotDelta sign", s => Math.Sign(s.DotDelta).ToString()),
				("EffectSpell != 0", s => (s.EffectSpell != 0).ToString()),
				("CastTime == 0", s => (s.CastTime == 0).ToString()),
				("RecastCs == 0", s => (s.RecastCentiseconds == 0).ToString()),
				("ManaCost == 0", s => (s.ManaCost == 0).ToString()),
				("ManaCostIsPercent", s => s.ManaCostIsPercent.ToString()),
				("ScalingAttribute", s => s.ScalingAttribute.ToString()),
				("GuiStackingLine == 0", s => (s.GuiStackingLine == 0).ToString()),
				("GuiStackingOrder", s => s.GuiStackingOrder.ToString()),
				("Range == 0", s => (s.Range == 0).ToString()),
				("AreaRadius == 0", s => (s.AreaRadius == 0).ToString()),
				("ChannelingIntervalMs == 0", s => (s.ChannelingIntervalMs == 0).ToString()),
				("has a SpellStatEffect", s => db.SpellStatEffects.Any(e => e.SpellId == s.Id).ToString()),
				("has a SpellEffect", s => db.SpellEffects.Any(e => e.SpellId == s.Id).ToString())
			};

			var withRow = perSpell.Where(s => spells.ContainsKey(s.SpellId)).ToList();
			foreach (var (field, value) in new (string, Func<SpellDescriptor, uint>)[] { ("descriptor[0]", s => s.Descriptor0), ("bool2", s => s.Bool2) })
			{
				var baseline = Purity(withRow.Select(s => ("", value(s))));
				Console.WriteLine($"{field} over {withRow.Count} catalogued spells - baseline (one constant) {baseline:P1}");
				foreach (var (name, key) in candidates)
				{
					var purity = Purity(withRow.Select(s => (key(spells[s.SpellId]), value(s))));
					var groups = withRow.Select(s => key(spells[s.SpellId])).Distinct().Count();
					Console.WriteLine($"    {name,-28} {purity,7:P1}  ({groups} groups){(purity > baseline + 0.02 ? "   <-- beats the baseline" : "")}");
				}

				Console.WriteLine();
			}

			Console.WriteLine($"{"spell",-46} {"n",5} {"d0",3} {"b2",3}  duration_ms  cast  dot  effect");
			foreach (var s in perSpell)
			{
				var r = spells.GetValueOrDefault(s.SpellId);
				Console.WriteLine($"{s.Name[..Math.Min(46, s.Name.Length)],-46} {s.Applies,5} {s.Descriptor0,3}{(s.Descriptor0Split ? "*" : " ")} {s.Bool2,2}{(s.Bool2Split ? "*" : " ")}  "
				                  + $"{r?.DurationMs,11}  {r?.CastTime,4}  {r?.DotType,3}  {r?.EffectSpell,6}");
			}

			Console.WriteLine();
			Console.WriteLine("* = the spell is SPLIT: retail sent more than one value for it, so no per-spell source can be the whole story.");
		}

		// `--report buff-descriptor --report-arg rdb`
		//
		// The other half of #658's route: the two fields are nearly per-SPELL, so ask the spell's own RDB 1040005
		// record whether anything in it partitions them. Same shape as `spell-mana-mode`'s sweep - the record's fixed
		// HEAD at word granularity (where #630's flag turned out to live, after three sweeps of the property run found
		// nothing) and every property field, both by value and bit by bit.
		//
		// ⚠️ A record carries a property field or it does not, and ABSENT is not zero. Absent records are dropped from
		// that field's tally rather than folded into the clear side, where they would manufacture agreement.
		private static void BuffDescriptorRdbSweep(IReadOnlyList<NamedCapture> captures, string rdbDir)
		{
			// The per-spell class: the modal value of each field, restricted to spells retail never split.
			var applies = new List<(uint SpellId, uint Descriptor0, byte Bool2)>();
			foreach (var capture in captures)
			foreach (var p in capture.Packets)
			{
				if (p is not { IsIirCarrier: true, SubOpcode: MagicBuffCodec.Opcode })
					continue;

				if (!p.TryParse(MagicBuffCodec.Parse, out var buff) || buff.Form != MagicBuffIds.FormApply || buff.Data.Length != MagicBuffDataCodec.Length)
					continue;

				var payload = MagicBuffDataCodec.Parse(buff.Data);
				applies.Add((buff.SpellBaseId, payload.Descriptor[0], payload.Bool2));
			}

			var classOf = new Dictionary<uint, (uint Descriptor0, uint Bool2)>();
			foreach (var g in applies.GroupBy(a => a.SpellId))
			{
				if (g.Select(a => a.Descriptor0).Distinct().Count() > 1 || g.Select(a => (uint)a.Bool2).Distinct().Count() > 1)
					continue; // split within the spell - it cannot be evidence for a per-spell source

				classOf[g.Key] = (g.First().Descriptor0, g.First().Bool2);
			}

			using var rdb = OpenRdb(rdbDir);
			var records = new List<(uint SpellId, byte[] Body, Dictionary<uint, uint> Fields)>();
			foreach (var e in rdb.EntriesOfType(SpellTemplateType))
			{
				if (e.Size == 0 || !classOf.ContainsKey(e.Id))
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

				if (body.Length < 48 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(body) != SpellTemplateType)
					continue;

				records.Add((e.Id, body, Rdb.RdbSpellImporter.ReadIncreasingFields(body.AsSpan(12))));
			}

			Console.WriteLine($"{classOf.Count} spells with an unsplit value; {records.Count} of them have a readable 1040005 record.");

			foreach (var which in new[] { "descriptor[0]", "bool2" })
			{
				var value = which == "descriptor[0]"
					? new Func<uint, uint>(id => classOf[id].Descriptor0)
					: id => classOf[id].Bool2;

				var histogram = records.GroupBy(r => value(r.SpellId)).OrderByDescending(g => g.Count()).ToList();
				var baseline = (double)histogram.Max(g => g.Count()) / records.Count;
				Console.WriteLine($"\n=== {which} === {string.Join(" ", histogram.Select(g => $"{g.Key}x{g.Count()}"))} - baseline {baseline:P1}");

				// 1. the fixed head, word by word.
				var headRanked = new List<(int Word, int Values, double Purity)>();
				for (var w = 0; w < HeadWords; w++)
				{
					var usable = records.Where(r => (w + 1) * 4 <= r.Body.Length).ToList();
					if (usable.Count < records.Count / 2)
						continue;

					var groups = usable.GroupBy(r => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(r.Body.AsSpan(w * 4, 4))).ToList();
					headRanked.Add((w, groups.Count, (double)groups.Sum(g => g.GroupBy(r => value(r.SpellId)).Max(v => v.Count())) / usable.Count));
				}

				Console.WriteLine($"  head words ({HeadWords}), by purity:");
				foreach (var h in headRanked.OrderByDescending(h => h.Purity).ThenBy(h => h.Values).Take(6))
					Console.WriteLine($"    word {h.Word,2}  {h.Purity,7:P1}  ({h.Values} distinct){(h.Purity > baseline + 0.02 ? "   <-- beats the baseline" : "")}");

				// The head word BY BIT, which is the shape #630's flag turned out to have: a packed flags word takes
				// dozens of values and reads as noise until you ask the bits one at a time.
				var headBits = new List<(int Word, int Bit, double Purity, int Set)>();
				for (var w = 0; w < HeadWords; w++)
				{
					var usable = records.Where(r => (w + 1) * 4 <= r.Body.Length).ToList();
					if (usable.Count < records.Count / 2)
						continue;

					for (var bit = 0; bit < 32; bit++)
					{
						var groups = usable.GroupBy(r => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(r.Body.AsSpan(w * 4, 4)) >> bit & 1).ToList();
						if (groups.Count < 2)
							continue;

						headBits.Add((w, bit, (double)groups.Sum(g => g.GroupBy(r => value(r.SpellId)).Max(v => v.Count())) / usable.Count,
							usable.Count(r => (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(r.Body.AsSpan(w * 4, 4)) >> bit & 1) == 1)));
					}
				}

				Console.WriteLine("  single BITS of a head word:");
				foreach (var b in headBits.OrderByDescending(b => b.Purity).Take(8))
					Console.WriteLine($"    word {b.Word,2} bit {b.Bit,2}  {b.Purity,7:P1}  ({b.Set} of {records.Count} set){(b.Purity > baseline + 0.02 ? "   <-- beats the baseline" : "")}");

				// 2. the property run, by value and then bit by bit.
				var fieldRanked = new List<(uint Field, int Covered, int Values, double Purity)>();
				foreach (var field in records.SelectMany(r => r.Fields.Keys).Distinct())
				{
					var carrying = records.Where(r => r.Fields.ContainsKey(field)).ToList();
					if (carrying.Count < 8)
						continue;

					var groups = carrying.GroupBy(r => r.Fields[field]).ToList();
					fieldRanked.Add((field, carrying.Count, groups.Count, (double)groups.Sum(g => g.GroupBy(r => value(r.SpellId)).Max(v => v.Count())) / carrying.Count));
				}

				Console.WriteLine("  property fields (>= 8 records carrying it), by purity:");
				foreach (var f in fieldRanked.OrderByDescending(f => f.Purity).ThenByDescending(f => f.Covered).Take(8))
					Console.WriteLine($"    field {f.Field,4}  {f.Purity,7:P1}  on {f.Covered,3} records, {f.Values} distinct");

				var bitRanked = new List<(uint Field, int Bit, int Covered, double Purity)>();
				foreach (var field in records.SelectMany(r => r.Fields.Keys).Distinct())
				{
					var carrying = records.Where(r => r.Fields.ContainsKey(field)).ToList();
					if (carrying.Count < 8)
						continue;

					for (var bit = 0; bit < 32; bit++)
					{
						var groups = carrying.GroupBy(r => r.Fields[field] >> bit & 1).ToList();
						if (groups.Count < 2)
							continue; // the bit never varies here, so it states nothing

						bitRanked.Add((field, bit, carrying.Count, (double)groups.Sum(g => g.GroupBy(r => value(r.SpellId)).Max(v => v.Count())) / carrying.Count));
					}
				}

				Console.WriteLine("  single BITS of a property field:");
				foreach (var b in bitRanked.OrderByDescending(b => b.Purity).ThenByDescending(b => b.Covered).Take(8))
					Console.WriteLine($"    field {b.Field,4} bit {b.Bit,2}  {b.Purity,7:P1}  on {b.Covered,3} records");

				// 3. every WORD of the whole record, head and tail alike (#632: ~87% of a record is outside the run).
				var maxWords = records.Min(r => r.Body.Length) / 4;
				var wordRanked = new List<(int Word, int Values, double Purity)>();
				for (var w = 0; w < maxWords; w++)
				{
					var groups = records.GroupBy(r => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(r.Body.AsSpan(w * 4, 4))).ToList();
					// A word with as many values as there are records separates them for free and says nothing.
					if (groups.Count > records.Count / 2)
						continue;

					wordRanked.Add((w, groups.Count, (double)groups.Sum(g => g.GroupBy(r => value(r.SpellId)).Max(v => v.Count())) / records.Count));
				}

				Console.WriteLine($"  every word of the common {maxWords}-word prefix (coarse ones only):");
				foreach (var w in wordRanked.OrderByDescending(w => w.Purity).ThenBy(w => w.Values).Take(8))
					Console.WriteLine($"    word {w.Word,4}  {w.Purity,7:P1}  ({w.Values} distinct){(w.Purity > baseline + 0.02 ? "   <-- beats the baseline" : "")}");
			}
		}

		private static uint Modal(IEnumerable<uint> values)
		{
			return values.GroupBy(v => v).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
		}

		private static string Histogram(IEnumerable<uint> values)
		{
			return string.Join(" ", values.GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}x{g.Count()}"));
		}

		// The share of rows that fall in their key group's majority value. 100% means the key determines the value.
		private static double Purity(IEnumerable<(string Key, uint Value)> rows)
		{
			var all = rows.ToList();
			return all.Count == 0 ? 0 : (double)all.GroupBy(r => r.Key).Sum(g => g.GroupBy(r => r.Value).Max(v => v.Count())) / all.Count;
		}

		private sealed record SpellDescriptor(uint SpellId, string Name, int Applies, uint Descriptor0, bool Descriptor0Split, uint Bool2, bool Bool2Split);
	}
}
