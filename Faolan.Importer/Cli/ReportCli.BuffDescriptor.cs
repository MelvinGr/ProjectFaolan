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
			// #658: (caster, target, spell, value) per apply - the input to the SCOPE statement below, which is the
			// measurement that named `bool2` and which no per-spell table can make.
			var scoped = new List<(uint Caster, uint Target, uint SpellId, uint Descriptor0)>();
			// #658: the same applies with enough CONTEXT to ask whether descriptor[0] is per-cast STATE rather than a
			// property of the spell - which capture, in what order, on whom, and the whole 54 bytes so the question
			// "does d0 gate any OTHER byte" can be asked of all of them instead of only [1] and [4].
			var sequenced = new List<(string Capture, int Order, uint Target, uint SpellId, uint Descriptor0, byte Bool2, byte[] Payload)>();
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
				if (buff.ApplyFrom is { } caster)
					scoped.Add((caster, buff.ApplyTo, buff.SpellBaseId, payload.Descriptor[0]));
				sequenced.Add((capture.Name, sequenced.Count, buff.ApplyTo, buff.SpellBaseId, payload.Descriptor[0], payload.Bool2, buff.Data.ToArray()));

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
			DescriptorZeroByteDiff(sequenced);
			DescriptorZeroReapplySequence(sequenced, spells);
			DescriptorProvenance(db, applies);

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

			ScopeStatement(scoped);
			TwinRate(perSpell);
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

		// The FLAGS word's bits against the MINORITY value, as a RATE rather than as purity.
		//
		// ⚠️ **A purity sweep structurally cannot see this**, which is why it sits beside one rather than inside it.
		// `descriptor[0]` is 2 on ~15% of spells, so a bit that lifts that to 60% - a decisive association - still
		// leaves 40% of its records in the majority class and scores BELOW an 84% baseline. Purity answers "does this
		// bit SPLIT the population cleanly"; the question here is "does it CONCENTRATE the rare value", and the two
		// come apart exactly when the value is rare. See the `purity_sweep_cannot_see_a_rate_lift` note.
		//
		// Word 13 is the flags word (#632, `docs/rdb/spell-record-fields.md`), so its bits are the ones with named
		// meanings to test against - bit 26 in particular, which marks the propagated/derived COPY of a buff and is
		// the obvious candidate for "this apply is not the cast a player performed".
		private static void MinorityRateLift(IReadOnlyList<(uint SpellId, byte[] Body, Dictionary<uint, uint> Fields)> records,
			Func<uint, uint> value, IReadOnlyList<IGrouping<uint, (uint SpellId, byte[] Body, Dictionary<uint, uint> Fields)>> histogram)
		{
			const int FlagsWord = 13;

			var usable = records.Where(r => (FlagsWord + 1) * 4 <= r.Body.Length).ToList();
			if (usable.Count == 0 || histogram.Count < 2)
				return;

			// The rarest value that is not a one-off: a class of 1 or 2 records has no rate to lift.
			var minority = histogram.Where(g => g.Count() >= 5).OrderBy(g => g.Count()).FirstOrDefault();
			if (minority == null)
				return;

			var overall = (double)usable.Count(r => value(r.SpellId) == minority.Key) / usable.Count;
			Console.WriteLine($"  word {FlagsWord} (the FLAGS word) by bit, as the RATE of the minority value {minority.Key} "
			                  + $"- overall {overall:P1} of {usable.Count}:");

			var rows = new List<(int Bit, int Set, double Rate)>();
			for (var bit = 0; bit < 32; bit++)
			{
				var set = usable.Where(r => (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(r.Body.AsSpan(FlagsWord * 4, 4)) >> bit & 1) == 1).ToList();
				if (set.Count < 5)
					continue;

				rows.Add((bit, set.Count, (double)set.Count(r => value(r.SpellId) == minority.Key) / set.Count));
			}

			foreach (var row in rows.OrderByDescending(r => r.Rate).Take(8))
			{
				var lift = row.Rate - overall;
				Console.WriteLine($"    bit {row.Bit,2}  {row.Rate,7:P1}  ({row.Set,3} set)   lift {lift,+7:P1}"
				                  + (lift > 0.15 ? "   <-- concentrates it" : ""));
			}
		}


		// ⭐ The SCOPE statement: does `descriptor[0]` belong to the SPELL, the CASTER, or the pair?
		//
		// This is the measurement that named `bool2` on this ticket after every content-based sweep had come back at
		// baseline, and it is worth having as a report rather than as a one-off because it answers a question no
		// per-spell table can ask. A per-spell table can only say "the spell does not determine it"; it cannot say
		// WHOSE field it is, and those are different findings.
		//
		// The form is a pair of conditional-variation rates:
		//   * of the casters that cast MORE THAN ONE distinct spell, how many vary the value?   -> 0 means caster-owned
		//   * of the spells cast by MORE THAN ONE distinct caster, how many vary it?            -> 0 means spell-owned
		//
		// ⚠️ **Both restrictions matter.** An entity that only ever casts one spell, or a spell only ever cast by one
		// entity, is consistent with EITHER owner and contributes nothing - counting it inflates whichever side you
		// were hoping for. `bool2` came out 0-of-159 caster-side against 4-of-128 spell-side, which is what a
		// caster-owned field looks like; a field owned by neither shows variation on both sides.

		// ⭐ Does `descriptor[0]`'s minority value mark ONE MEMBER of a same-NAME family?
		//
		// The same measurement that named the spell record's flags bit 26 (#632): a value that marks the derived copy
		// of a spell shows up as "almost every record carrying it has a same-name sibling that does NOT". Several of
		// the `descriptor[0]=2` spells here are one of several ids sharing a name - `Blessed Blade` is four ids,
		// `Furious Inspiration` five - so the question is whether that is the rule or just what a rank family looks
		// like.
		//
		// ⚠️ The baseline is what makes it readable: rank families are common in this catalog, so a high twin rate is
		// only evidence if the value's twin rate BEATS the catalog's. Printed together for that reason.
		private static void TwinRate(IReadOnlyList<SpellDescriptor> perSpell)
		{
			var byName = perSpell.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.ToList());
			bool Twinned(SpellDescriptor s, Func<SpellDescriptor, bool> lacks) => byName[s.Name].Any(o => o.SpellId != s.SpellId && lacks(o));

			Console.WriteLine();
			Console.WriteLine("does descriptor[0] mark one member of a same-NAME family? (the #632 bit-26 test)");
			foreach (var value in perSpell.Select(s => s.Descriptor0).Distinct().OrderBy(v => v))
			{
				var carrying = perSpell.Where(s => s.Descriptor0 == value).ToList();
				var twinned = carrying.Count(s => Twinned(s, o => o.Descriptor0 != value));
				Console.WriteLine($"  descriptor[0]={value}: {twinned,3} of {carrying.Count,3} have a same-name spell with a DIFFERENT value"
				                  + $"   ({(carrying.Count == 0 ? 0 : (double)twinned / carrying.Count):P1})");
			}

			var anyTwin = perSpell.Count(s => byName[s.Name].Count > 1);
			Console.WriteLine($"  baseline: {anyTwin} of {perSpell.Count} spells share their name with another at all "
			                  + $"({(double)anyTwin / perSpell.Count:P1}) - a value cannot beat this by much and mean anything.");
		}

		private static void ScopeStatement(IReadOnlyList<(uint Caster, uint Target, uint SpellId, uint Descriptor0)> scoped)
		{
			if (scoped.Count == 0)
				return;

			Console.WriteLine();
			Console.WriteLine($"SCOPE of descriptor[0] over {scoped.Count:N0} applies that name a caster:");

			foreach (var (label, key, other) in new (string, Func<(uint Caster, uint Target, uint SpellId, uint Descriptor0), uint>,
				         Func<(uint Caster, uint Target, uint SpellId, uint Descriptor0), uint>)[]
			         {
				         ("caster", a => a.Caster, a => a.SpellId),
				         ("spell", a => a.SpellId, a => a.Caster),
				         ("target", a => a.Target, a => a.SpellId)
			         })
			{
				// Only groups that actually VARY the other dimension can testify.
				var groups = scoped.GroupBy(key).Where(g => g.Select(other).Distinct().Count() > 1).ToList();
				var varying = groups.Count(g => g.Select(a => a.Descriptor0).Distinct().Count() > 1);
				Console.WriteLine($"  of the {groups.Count,4} {label,-6} that span >1 other, {varying,4} vary descriptor[0]"
				                  + $"   ({(groups.Count == 0 ? 0 : (double)varying / groups.Count):P1})"
				                  + (groups.Count > 0 && varying == 0 ? "   <-- the field belongs to this" : ""));
			}
		}


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

				MinorityRateLift(records, value, histogram);

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

		// #658: does `descriptor[0]` gate ANY other byte of the payload? The existing cross-tab asks that of slots
		// [1] and [4] only, because those are the two that are near-zero rather than zero - which cannot see a byte
		// that is populated on BOTH values and merely differs. This asks it of all 54: for each byte, the value sets
		// under d0=1 and d0=2, and whether they are disjoint.
		//
		// ⚠️ A byte that varies freely (the level, the duration, the stamp) is NOISE here, not a discriminator - it
		// is printed only when the two value sets do not overlap at all, which is what "gates" means.
		private static void DescriptorZeroByteDiff(List<(string Capture, int Order, uint Target, uint SpellId, uint Descriptor0, byte Bool2, byte[] Payload)> applies)
		{
			var ones = applies.Where(a => a.Descriptor0 == 1).ToList();
			var twos = applies.Where(a => a.Descriptor0 == 2).ToList();
			if (ones.Count == 0 || twos.Count == 0)
				return;

			Console.WriteLine($"does descriptor[0] GATE another byte? ({ones.Count} applies at d0=1 against {twos.Count} at d0=2, all {MagicBuffDataCodec.Length} bytes)");
			var found = 0;
			for (var i = 0; i < MagicBuffDataCodec.Length; i++)
			{
				var a = ones.Select(x => x.Payload[i]).ToHashSet();
				var b = twos.Select(x => x.Payload[i]).ToHashSet();
				if (a.Overlaps(b))
					continue;

				found++;
				Console.WriteLine($"  byte @{i,2}: d0=1 {{{string.Join(",", a.Order().Take(8))}}}  d0=2 {{{string.Join(",", b.Order().Take(8))}}}");
			}

			Console.WriteLine(found == 0
				? "  none - every byte's value set overlaps, so descriptor[0] gates nothing in the payload."
				: $"  {found} byte(s) separate the two values.");
			Console.WriteLine();
		}

		// #658: is `descriptor[0]` a per-cast STACK COUNT? The spells that carry 2 read like the game's stacking
		// buffs (Wrath, Momentum, Furious Inspiration, Mutilation, Blessed Blade), and two of them are named with the
		// stack in the name - `Furious Inspiration (6)`, `Mutilation (1)`. If it counts, a RE-application to the same
		// target while the buff is up should carry a higher number than the one before it.
		//
		// Grouped by (capture, target, spell): a handle is session-scoped, so grouping on the handle alone merges
		// unrelated entities across captures - the same rule the level-slot measurement needed.
		private static void DescriptorZeroReapplySequence(List<(string Capture, int Order, uint Target, uint SpellId, uint Descriptor0, byte Bool2, byte[] Payload)> applies,
			Dictionary<uint, Core.Database.Tables.Spell> spells)
		{
			var runs = applies
				.GroupBy(a => (a.Capture, a.Target, a.SpellId))
				.Where(g => g.Count() > 1)
				.Select(g => (Key: g.Key, Values: g.OrderBy(a => a.Order).Select(a => a.Descriptor0).ToList()))
				.ToList();

			var rising = runs.Count(r => IsStrictlyRisingSomewhere(r.Values));
			var constant = runs.Count(r => r.Values.Distinct().Count() == 1);
			Console.WriteLine($"is descriptor[0] a per-cast STACK COUNT? {runs.Count} re-application runs (same capture, target and spell, 2+ applies)");
			Console.WriteLine($"  constant across the run : {constant}");
			Console.WriteLine($"  rises at least once     : {rising}");
			Console.WriteLine($"  starts at 1             : {runs.Count(r => r.Values[0] == 1)}");
			foreach (var run in runs.Where(r => r.Values.Distinct().Count() > 1).Take(15))
			{
				var name = spells.TryGetValue(run.Key.SpellId, out var s) ? s.Name : $"0x{run.Key.SpellId:X8}";
				Console.WriteLine($"    {name,-34} {string.Join(",", run.Values)}   ({run.Key.Capture})");
			}

			Console.WriteLine();
		}

		private static bool IsStrictlyRisingSomewhere(List<uint> values)
		{
			for (var i = 1; i < values.Count; i++)
				if (values[i] > values[i - 1])
					return true;

			return false;
		}

		// #658: how each value of `descriptor[0]` / `bool2` splits on the spell's PROVENANCE - can the caster cast it,
		// or does it only arrive through something else? Measured as a RATE OVER APPLIES rather than as the purity of
		// a partition over spells, which is why the existing sweep could not see it: with an 83.5% baseline a purity
		// score needs a near-perfect partition, and a 3x rate lift scores at the baseline.
		//
		// ⚠️ A rate lift is a LEAD, not a decode. None of these is a partition, so the builder still writes the
		// constant - what they buy is a reading with numbers behind it instead of a list of spell names.
		private static void DescriptorProvenance(Core.Database.DatabaseContext db, List<(uint SpellId, uint Descriptor0, byte Bool2)> applies)
		{
			// "Reachable as another spell's EFFECT" is the cut the earlier membership sweep did not include: it swept
			// ClassSpells, Combos.GameActionId, SpellEffects, SpellStatEffects, NpcSpells and FeatSpellGrants, but not
			// the chain/area target column, which is the one that separates.
			var chained = db.SpellAreaEffects.Where(e => e.EffectSpellId != 0).Select(e => e.EffectSpellId).Distinct().ToHashSet();
			var castable = db.ClassSpells.Select(c => c.SpellId).Distinct().ToHashSet();
			var statEffect = db.SpellStatEffects.Select(e => e.SpellId).Distinct().ToHashSet();

			var cuts = new (string Name, Func<uint, bool> Holds)[]
			{
				("reachable as another spell's effect", id => chained.Contains(id)),
				("castable (a ClassSpells row)", id => castable.Contains(id)),
				("ONLY reachable indirectly", id => chained.Contains(id) && !castable.Contains(id)),
				("carries a SpellStatEffect", id => statEffect.Contains(id))
			};

			foreach (var (field, value) in new (string, Func<(uint SpellId, uint Descriptor0, byte Bool2), uint>)[]
			         { ("descriptor[0]", a => a.Descriptor0), ("bool2", a => a.Bool2) })
			{
				Console.WriteLine($"{field} against the spell's PROVENANCE, as a rate over APPLIES:");
				var byValue = applies.GroupBy(value).OrderByDescending(g => g.Count()).Take(3).ToList();
				foreach (var (name, holds) in cuts)
				{
					var cells = byValue.Select(g => $"{field.Split('[')[0]}={g.Key}: {g.Count(a => holds(a.SpellId)),5}/{g.Count(),5} = {100.0 * g.Count(a => holds(a.SpellId)) / g.Count(),5:0.0}%");
					Console.WriteLine($"  {name,-38}  {string.Join("   ", cells)}");
				}

				Console.WriteLine();
			}
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
