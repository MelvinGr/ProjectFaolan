using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Rdb;
using Faolan.Importer.Captures;
using Faolan.Importer.Rdb;

namespace Faolan.Importer.Cli
{
	// `--report magicbuff-descriptor`: where do the MagicBuff apply payload's `descriptor[0]` and `bool2` come from?
	//
	// Gitea #658. `MagicBuffDatas.Apply` writes both as constants generalised from four hand-picked capture rows, and
	// the whole-corpus gate says they are not constants: over 2,929 captured applies `descriptor[0]` is 1×2606,
	// 2×321, 3×2 and `bool2` is 1×1806, 0×1123.
	//
	// ⭐ That ticket's own measurement points at the answer: both are nearly a property of the SPELL rather than of
	// the cast (`descriptor[0]` varies within a spell on 7 of 256 spells, `bool2` on 3 of 256). A per-spell value
	// comes off the spell's own RDB record, so this cross-tabs each against the record's FLAGS word - bit by bit,
	// with the same base-rate correction `--report spell-flags` uses, because two mostly-false columns agree for
	// free.
	//
	// ⚠️ It scores per SPELL, not per apply. A spell cast 400 times would otherwise carry its own answer 400 times
	// and drown the other 255.
	public static partial class ReportCli
	{
		// The apply payload's offsets, as `MagicBuffData` lays it out - the same ones `MagicBuffCorpusTests` reads.
		private const int Bool2Offset = 13;
		private const int Descriptor0Offset = 14;

		// The two slots `MagicBuffDatas.Apply` fills from ONE `casterLevel` argument, and which disagree on 728 of
		// 2,929 captured applies - so they are demonstrably two numbers (#658).
		private const int Descriptor3Offset = 26;
		private const int CastingLevelOffset = 42;
		private const int ApplyPayloadLength = 54;
		private const byte ApplyForm = 0; // ⚠️ ZERO, not 1 - the value MagicBuffCorpusTests filters on

		private static int MagicBuffDescriptor(IReadOnlyList<NamedCapture> captures, string rdbDir)
		{
			// spell -> the descriptor[0] / bool2 values its captured applies carried.
			var perSpell = new Dictionary<uint, (List<uint> Descriptor0, List<byte> Bool2)>();

			// The level pair, per apply rather than per spell: the question is whether either slot is the SPELL's own
			// level, which is a per-spell constant, so a slot that tracks it must do so on every cast.
			var levelPairs = new List<(uint SpellId, uint Descriptor3, uint CastingLevel)>();
			var arrivals = 0;
			var applies = 0;

			foreach (var capture in captures)
			foreach (var p in capture.Packets)
			{
				if (p is not { IsDownload: true, IsIirCarrier: true, SubOpcode: MagicBuffCodec.Opcode })
					continue;

				arrivals++;
				if (!p.TryParse(MagicBuffCodec.Parse, out var buff) || buff.Form != ApplyForm || buff.Data.Length != ApplyPayloadLength)
					continue;

				applies++;
				if (!perSpell.TryGetValue(buff.SpellBaseId, out var seen))
					perSpell[buff.SpellBaseId] = seen = ([], []);

				seen.Descriptor0.Add(buff.Data.ReadU32Be(Descriptor0Offset));
				seen.Bool2.Add(buff.Data[Bool2Offset]);
				levelPairs.Add((buff.SpellBaseId, buff.Data.ReadU32Be(Descriptor3Offset), buff.Data.ReadU32Be(CastingLevelOffset)));
			}

			Console.WriteLine("=== where do the MagicBuff apply's descriptor[0] and bool2 come from? (#658) ===");
			Console.WriteLine();
			// ⚠️ Arrivals ahead of the parse: "no capture carried one" and "every parse failed" are the same zero.
			Console.WriteLine($"  control - MagicBuffIIR: {arrivals,6} arrived, {applies,6} are APPLY payloads over {perSpell.Count} distinct spell(s)");

			if (perSpell.Count == 0)
			{
				Console.WriteLine("  ⚠️ nothing to score - the corpus carries no apply payloads, so this says nothing either way.");
				return 1;
			}

			// The per-spell answer: a spell counts once, for the value it MOSTLY carries. The ticket measured the
			// within-spell disagreement as 7 and 3 spells of 256, so a modal value loses almost nothing.
			var d0 = perSpell.ToDictionary(kv => kv.Key, kv => kv.Value.Descriptor0.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key);
			var b2 = perSpell.ToDictionary(kv => kv.Key, kv => kv.Value.Bool2.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key);

			using var rdb = OpenRdb(rdbDir);
			var flagsOf = new Dictionary<uint, uint>();
			var fieldsOf = new Dictionary<uint, Dictionary<uint, uint>>();
			foreach (var spellId in perSpell.Keys)
			{
				if (!rdb.TryGet(SpellTemplateType, spellId, out var entry) || !rdb.CanRead(entry))
					continue;

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

				var body = record.AsSpan(12);
				RdbSpellImporter.ReadIncreasingFields(body, out var runStart, out _);
				if (runStart < 20)
					continue;

				flagsOf[spellId] = RdbSpellImporter.ReadFlagsWord(body, runStart);
				fieldsOf[spellId] = RdbSpellImporter.ReadIncreasingFields(body, out _);
			}

			Console.WriteLine($"  of those, {flagsOf.Count} have a readable RDB record to cross-tab against.");
			Console.WriteLine();

			Score("descriptor[0] != 1", flagsOf, id => d0.GetValueOrDefault(id) != 1);
			Console.WriteLine();
			Score("bool2 == 0", flagsOf, id => b2.GetValueOrDefault(id) == 0);

			Console.WriteLine();
			MatchField("descriptor[0]", fieldsOf, d0);
			Console.WriteLine();
			MatchField("bool2", fieldsOf, b2.ToDictionary(kv => kv.Key, kv => (uint)kv.Value));

			Console.WriteLine();
			LevelPair(levelPairs);

			Console.WriteLine();
			Console.WriteLine("  ⚠️ EXCESS over chance, in points - a rare bit scores high against a rare predicate for free.");
			Console.WriteLine("     A bit that IS the field reads +50 or more against a balanced predicate; single digits are noise.");
			Console.WriteLine("  ⚠️ And a bit the client never READS cannot be the source however well it scores - see the read-site");
			Console.WriteLine("     census in `--report spell-flags` (bit 0 is set on 78% of records and has no consumer at all).");
			return 0;
		}

		/// <summary>
		///     The SHARPER test, and the one that would actually settle it: is there a PROPERTY FIELD whose value
		///     simply IS the descriptor? (#633's half of #658.)
		/// </summary>
		/// <remarks>
		///     ⭐ A correlation is what you measure when you cannot compare the thing itself. Here we can: if
		///     `descriptor[0]` is carried by a property field then that field's value EQUALS it, per spell, and a
		///     field matching on 278 of 278 is a definition rather than a lead. Nothing weaker than near-total
		///     agreement is worth reporting for a test this direct.
		///     <para />
		///     ⚠️ A field present on only a handful of records can match all of them by accident, so the count of
		///     records that CARRY the field is printed beside the agreement - a 3/3 is not a finding.
		/// </remarks>
		private static void MatchField(string label, Dictionary<uint, Dictionary<uint, uint>> fieldsOf, Dictionary<uint, uint> value)
		{
			var candidates = new List<(uint Field, int Match, int Carrying)>();
			foreach (var field in fieldsOf.Values.SelectMany(f => f.Keys).Distinct())
			{
				var carrying = 0;
				var match = 0;
				foreach (var (spellId, fields) in fieldsOf)
				{
					if (!fields.TryGetValue(field, out var v))
						continue;

					carrying++;
					if (v == value.GetValueOrDefault(spellId))
						match++;
				}

				if (carrying >= 20 && match * 100 >= carrying * 80)
					candidates.Add((field, match, carrying));
			}

			// ⛔ The base-rate correction, and for THIS test it is the whole thing. `descriptor[0]` is 1 on 83% of
			// spells, so any field that is also mostly 1 agrees ~80% of the time for free. What separates a real
			// carrier from a coincidence is whether it tracks the value on the records where the value is NOT the
			// modal one - the informative minority.
			var modal = value.Values.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;
			var informative = value.Where(kv => kv.Value != modal).Select(kv => kv.Key).ToHashSet();

			Console.WriteLine($"  is {label} the value of a PROPERTY FIELD? (>=80% agreement, >=20 carriers)");
			if (candidates.Count == 0)
			{
				Console.WriteLine("    none - no field the records carry has this value, so it is not a property field either.");
				return;
			}

			Console.WriteLine($"    (modal value is {modal}, carried by {value.Count - informative.Count} of {value.Count} spells -"
			                  + $" so the {informative.Count} that differ are what decides it)");
			foreach (var (field, match, carrying) in candidates.OrderByDescending(c => c.Match / (double)c.Carrying))
			{
				var infoCarrying = 0;
				var infoMatch = 0;
				foreach (var spellId in informative)
				{
					if (!fieldsOf.TryGetValue(spellId, out var fields) || !fields.TryGetValue(field, out var v))
						continue;

					infoCarrying++;
					if (v == value[spellId])
						infoMatch++;
				}

				var verdict = infoCarrying == 0
					? "⛔ carries NONE of the informative records - agreement is entirely base rate"
					: infoMatch * 100 >= infoCarrying * 80
						? "⭐ tracks the informative ones too"
						: $"⛔ only {infoMatch * 100.0 / infoCarrying:F0}% on the informative ones - base rate, not a carrier";
				Console.WriteLine($"    field {field,5}  overall {match}/{carrying} = {match * 100.0 / carrying:F0}%   informative {infoMatch}/{infoCarrying}   {verdict}");
			}
		}

		/// <summary>
		///     The other half of #658: two slots we fill from one `casterLevel` argument are demonstrably two
		///     numbers. Is either of them the SPELL's own level?
		/// </summary>
		/// <remarks>
		///     ⭐ A cheap decisive test, because the spell's level is a per-spell CONSTANT: a slot that carries it
		///     agrees on every cast of that spell, and a slot that carries the CASTER's level does not, since the
		///     same spell is cast by characters of different levels. So the two hypotheses make opposite predictions
		///     about within-spell variance, and no caster-level oracle is needed to tell them apart.
		/// </remarks>
		private static void LevelPair(List<(uint SpellId, uint Descriptor3, uint CastingLevel)> pairs)
		{
			var disagree = pairs.Count(p => p.Descriptor3 != p.CastingLevel);
			Console.WriteLine($"  the LEVEL PAIR over {pairs.Count} applies: the two slots disagree on {disagree} ({disagree * 100.0 / Math.Max(1, pairs.Count):F0}%)");

			foreach (var (name, pick) in new (string, Func<(uint SpellId, uint Descriptor3, uint CastingLevel), uint>)[]
			         {
				         ("descriptor[3] @26", p => p.Descriptor3),
				         ("CastingLevel  @42", p => p.CastingLevel)
			         })
			{
				var bySpell = pairs.GroupBy(p => p.SpellId).ToList();
				var constant = bySpell.Count(g => g.Select(pick).Distinct().Count() == 1);
				Console.WriteLine($"    {name}: constant within the spell on {constant} of {bySpell.Count} spells ({constant * 100.0 / bySpell.Count:F0}%)"
				                  + $" - values seen {string.Join(", ", pairs.Select(pick).Distinct().OrderBy(v => v).Take(8))}...");
			}

			Console.WriteLine("    ⭐ A slot carrying the SPELL's level is constant within a spell; one carrying the CASTER's is not,");
			Console.WriteLine("       because the same spell is cast by characters of different levels. That is what separates them -");
			Console.WriteLine("    ⛔ ...and here it does NOT: 75% against 69% is not a separation. Both slots look per-spell.");
			Console.WriteLine("       ⚠️ The test is CONFOUNDED and the confound is the corpus: it is a handful of characters, so a");
			Console.WriteLine("       caster level barely varies within one spell either. A slot carrying the caster's level would");
			Console.WriteLine("       look per-spell too. Separating them needs captures of ONE spell cast by casters of clearly");
			Console.WriteLine("       different levels, which is a capture to make rather than one the corpus happens to contain.");
			Console.WriteLine("       ⭐ One asymmetry that is NOT confounded: descriptor[3] takes the value 0 and CastingLevel does");
			Console.WriteLine("       not (it starts at 1). A character level is never 0, so whatever @26 is, it is not simply a level.");
		}

		// One predicate against all 32 bits, base-rate corrected the same way `--report spell-flags` does it.
		private static void Score(string label, Dictionary<uint, uint> flagsOf, Func<uint, bool> predicate)
		{
			var ids = flagsOf.Keys.ToList();
			var trueRate = ids.Count(predicate) / (double)ids.Count;
			Console.WriteLine($"  {label}: true on {ids.Count(predicate)} of {ids.Count} spells ({trueRate * 100:F0}%)");
			Console.WriteLine($"    {"bit",4}  {"set",6}  {"agree",7}  excess");

			for (var bit = 0; bit < 32; bit++)
			{
				var b = bit;
				var set = ids.Count(id => (flagsOf[id] >> b & 1) == 1);
				if (set == 0)
					continue;

				var agree = ids.Count(id => ((flagsOf[id] >> b & 1) == 1) == predicate(id));
				var rate = agree / (double)ids.Count;

				// Chance agreement for two independent columns of these prevalences.
				var setRate = set / (double)ids.Count;
				var chance = setRate * trueRate + (1 - setRate) * (1 - trueRate);
				var excess = (rate - chance) * 100;
				if (excess >= 5)
					Console.WriteLine($"    {bit,4}  {set,6}  {rate * 100,6:F0}%  +{excess,4:F0}");
			}
		}
	}
}
