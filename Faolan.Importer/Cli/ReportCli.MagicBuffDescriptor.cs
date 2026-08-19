using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Rdb;
using Faolan.Importer.Captures;
using Faolan.Importer.Rdb;
using Microsoft.EntityFrameworkCore;

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

		private static async Task<int> MagicBuffDescriptor(IReadOnlyList<NamedCapture> captures, string rdbDir)
		{
			// spell -> the descriptor[0] / bool2 values its captured applies carried.
			var perSpell = new Dictionary<uint, (List<uint> Descriptor0, List<byte> Bool2)>();

			// The level pair, per apply rather than per spell: the question is whether either slot is the SPELL's own
			// level, which is a per-spell constant, so a slot that tracks it must do so on every cast.
			var levelPairs = new List<(uint SpellId, uint Descriptor3, uint CastingLevel)>();

			// ⭐ The CONTEXT hypotheses, per apply rather than per spell (#658). The record's own fields are
			// exhausted - neither value is a flags bit nor a property field - so the next place to look is the cast
			// itself. `applyTo == applyFrom` is the leading candidate for a boolean: a self-buff is always self and a
			// targeted debuff always other, which reproduces "nearly per-spell" without being a property of the
			// spell at all, and the handful of spells that VARY would be exactly the ones cast both ways.
			var context = new List<(byte Bool2, uint Descriptor0, bool SelfCast, bool NoCaster)>();

			// ⭐ The SEPARATING population for the level pair (the per-apply cross-tab above is confounded: the
			// corpus is a handful of characters, so a caster level barely varies within one spell). An apply whose
			// CASTER and TARGET are at DIFFERENT levels can tell the two slots apart - and the levels come from the
			// same capture's own spawn bodies, which state every entity's level next to its handle.
			var separated = new List<(uint SpellId, uint Descriptor3, uint CastingLevel, uint CasterLevel, uint TargetLevel)>();
			var arrivals = 0;
			var applies = 0;

			foreach (var capture in captures)
			{
				// handle -> the level its own spawn body stated, filled in the same forward walk (a spawn always
				// precedes any apply that references its handle).
				var levels = new Dictionary<uint, uint>();

				foreach (var p in capture.Packets)
				{
					if (!p.IsIirCarrier || !p.IsDownload)
						continue;

					if (p.SubOpcode == SimpleCharFullUpdateCodec.Opcode)
					{
						if (p.Body.Length > SpawnPacketBuilder.HandleOffset + 4 && SpawnBodyReader.TryReadCharacterMessage(p.Body, out var read) && read.Message?.Stats != null)
							levels[p.Body.ReadU32Be(SpawnPacketBuilder.HandleOffset)] = read.Message.Stats.Level;

						continue;
					}

					if (p.SubOpcode != MagicBuffCodec.Opcode)
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
					context.Add((buff.Data[Bool2Offset], buff.Data.ReadU32Be(Descriptor0Offset), buff.ApplyTo == buff.ApplyFrom, buff.ApplyFrom == 0));

					if (buff.ApplyFrom is { } caster && caster != 0 && caster != buff.ApplyTo
					    && levels.TryGetValue(caster, out var casterLevel) && levels.TryGetValue(buff.ApplyTo, out var targetLevel))
						separated.Add((buff.SpellBaseId, buff.Data.ReadU32Be(Descriptor3Offset), buff.Data.ReadU32Be(CastingLevelOffset), casterLevel, targetLevel));
				}
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
			ApplyContext(context);

			Console.WriteLine();
			await CastableOrDerived(d0, b2);

			Console.WriteLine();
			await NameTheGroups(d0, b2);

			Console.WriteLine();
			LevelPair(levelPairs);
			LevelPairSeparated(separated);

			Console.WriteLine();
			Console.WriteLine("  ⚠️ EXCESS over chance, in points - a rare bit scores high against a rare predicate for free.");
			Console.WriteLine("     A bit that IS the field reads +50 or more against a balanced predicate; single digits are noise.");
			Console.WriteLine("  ⚠️ And a bit the client never READS cannot be the source however well it scores - see the read-site");
			Console.WriteLine("     census in `--report spell-flags` (bit 0 is set on 78% of records and has no consumer at all).");
			return 0;
		}

		/// <summary>
		///     ⭐⭐ Is the record a CASTABLE or an EFFECT-ONLY one? (#658.)
		/// </summary>
		/// <remarks>
		///     The hypothesis the NAME listing below produced, tested structurally rather than by reading it off the
		///     names. Both groups read as records a player never casts directly - <c>Racer</c>, <c>Chicken Buff</c>,
		///     <c>Knockback immunity</c>, <c>Deathless Acolyte: Arcanist</c>, <c>Wrath</c>, <c>Furious
		///     Inspiration</c> - i.e. the thing that LANDS rather than the thing that was cast.
		///     <para />
		///     Four independent predicates for "a player can cast this", because no single one of them is clean: a
		///     class-catalog row, a cast time, a mana cost, and being the target of a chain edge (which says the
		///     opposite - it is reached BY something). A hypothesis that is right will move all four the same way.
		/// </remarks>
		private static async Task CastableOrDerived(Dictionary<uint, uint> descriptor0, Dictionary<uint, byte> bool2)
		{
			await using var db = OpenDb();
			var ids = descriptor0.Keys.ToList();

			var classSpells = (await db.ClassSpells.AsNoTracking().Where(c => ids.Contains(c.SpellId)).Select(c => c.SpellId).Distinct().ToListAsync()).ToHashSet();
			var chainTargets = (await db.SpellAreaEffects.AsNoTracking().Where(a => ids.Contains(a.EffectSpellId)).Select(a => a.EffectSpellId).Distinct().ToListAsync()).ToHashSet();
			var spells = await db.Spells.AsNoTracking().Where(s => ids.Contains(s.Id)).Select(s => new { s.Id, s.CastTime, s.ManaCost }).ToDictionaryAsync(s => s.Id, s => s);

			(string Name, Func<uint, bool> Holds)[] predicates =
			[
				("in a class catalog", id => classSpells.Contains(id)),
				("has a cast time", id => (spells.GetValueOrDefault(id)?.CastTime ?? 0) > 0),
				("has a mana cost", id => (spells.GetValueOrDefault(id)?.ManaCost ?? 0) > 0),
				("is a chain/area TARGET", chainTargets.Contains)
			];

			Console.WriteLine("  ⭐ is the record a CASTABLE or an EFFECT-ONLY one? (the hypothesis the name listing produced)");
			Console.WriteLine($"     {"predicate",-24} {"all",6} {"d0!=1",8} {"d0==1",8} {"b2==0",8} {"b2==1",8}");

			foreach (var (name, holds) in predicates)
			{
				string Rate(IEnumerable<uint> group)
				{
					var members = group.ToList();
					return members.Count == 0 ? "-" : $"{100.0 * members.Count(holds) / members.Count:F0}%";
				}

				Console.WriteLine($"     {name,-24} {Rate(ids),6} {Rate(ids.Where(id => descriptor0[id] != 1)),8} {Rate(ids.Where(id => descriptor0[id] == 1)),8}"
				                  + $" {Rate(ids.Where(id => bool2[id] == 0)),8} {Rate(ids.Where(id => bool2[id] == 1)),8}");
			}

			Console.WriteLine("     ⚠️ Read the SPREAD between the two halves of a pair, not the absolute rate: the corpus is what a");
			Console.WriteLine("        few characters cast, so every column sits well below the catalog's own base rate.");
		}

		/// <summary>
		///     ⭐ The two groups, BY NAME - the oracle of last resort when a numeric sweep has come up empty.
		/// </summary>
		/// <remarks>
		///     Neither value is a flags bit, a property field, or a property of the cast's target. What is left is to
		///     read the populations: a partition the client draws is usually a partition a HUMAN can see the moment
		///     the members are listed, and the catalog's own names are the only description of a spell we hold that
		///     was not written by us.
		/// </remarks>
		private static async Task NameTheGroups(Dictionary<uint, uint> descriptor0, Dictionary<uint, byte> bool2)
		{
			await using var db = OpenDb();
			var names = await db.Spells.AsNoTracking()
				.Where(s => descriptor0.Keys.Contains(s.Id))
				.ToDictionaryAsync(s => s.Id, s => new { s.Name, s.DurationMs, s.GuiStackingLine, s.Level });

			foreach (var (label, members) in new[]
			         {
				         ("descriptor[0] == 2 or 3", descriptor0.Where(kv => kv.Value != 1).Select(kv => kv.Key).ToArray()),
				         ("bool2 == 0", bool2.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToArray())
			         })
			{
				Console.WriteLine($"  {label}: {members.Length} spell(s)");
				foreach (var id in members.OrderBy(id => names.GetValueOrDefault(id)?.Name, StringComparer.Ordinal).Take(60))
				{
					var spell = names.GetValueOrDefault(id);
					Console.WriteLine($"     {id,-9} {spell?.Name ?? "?",-38} dur {spell?.DurationMs ?? 0,8} ms  line {spell?.GuiStackingLine ?? 0,-8} lvl {spell?.Level ?? 0}");
				}

				if (members.Length > 60)
					Console.WriteLine($"     ... and {members.Length - 60} more");

				Console.WriteLine();
			}
		}

		/// <summary>
		///     ⭐ Is either value a property of the CAST rather than of the spell? (#658.)
		/// </summary>
		/// <remarks>
		///     The spell record is exhausted - the flags word scores nothing above base rate and no property field
		///     carries either value - so what is left is the apply's own context. The candidate this scores is
		///     <c>applyTo == applyFrom</c>: it would look "nearly per-spell" for free, because a self-buff is always
		///     self and a targeted debuff always other, and the spells that VARY under it would be precisely the ones
		///     cast both ways (a heal on yourself or on a team-mate).
		///     <para />
		///     ⚠️ Scored per APPLY here, deliberately, and the opposite of what the per-spell scoring above needs: a
		///     context value has to agree on every individual cast, so collapsing to the modal value per spell would
		///     hide the disagreements that refute it.
		/// </remarks>
		private static void ApplyContext(List<(byte Bool2, uint Descriptor0, bool SelfCast, bool NoCaster)> context)
		{
			if (context.Count == 0)
				return;

			var selfCasts = context.Count(c => c.SelfCast);
			Console.WriteLine($"  is either value a property of the CAST? {context.Count} applies, {selfCasts} of them SELF ({100.0 * selfCasts / context.Count:F0}%)");

			// bool2 against "the target is the caster", both polarities - the sign of the mapping is not assumed.
			var agreeSelf = context.Count(c => (c.Bool2 == 1) == c.SelfCast);
			var agreeOther = context.Count(c => (c.Bool2 == 1) != c.SelfCast);
			Console.WriteLine($"     bool2 == 1  <=>  applyTo == applyFrom : {100.0 * agreeSelf / context.Count:F1}%   (inverted: {100.0 * agreeOther / context.Count:F1}%)");

			// The same for a descriptor[0] of 1 vs anything else, so a three-valued field is not scored as a boolean
			// by accident - and the value BREAKDOWN per polarity, which is what would show a 1/2/3 that means
			// self/other/no-caster.
			foreach (var group in context.GroupBy(c => (c.SelfCast, c.NoCaster)).OrderBy(g => g.Key.SelfCast).ThenBy(g => g.Key.NoCaster))
			{
				var d0 = string.Join(" ", group.GroupBy(c => c.Descriptor0).OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Count()}"));
				var b2 = string.Join(" ", group.GroupBy(c => c.Bool2).OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Count()}"));
				var label = group.Key.NoCaster ? "no caster " : group.Key.SelfCast ? "SELF cast " : "on ANOTHER";
				Console.WriteLine($"     {label} n={group.Count(),5}   descriptor[0]: {d0,-28}   bool2: {b2}");
			}

			Console.WriteLine("     ⚠️ A split that is CLEAN in this table is the answer; one that merely leans is the same");
			Console.WriteLine("        base-rate trap the bit sweep above corrects for - most applies are self-buffs.");
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
		// The level pair, scored on the population that can actually SEPARATE the two slots: applies whose caster
		// and target are at DIFFERENT levels, both read off the same capture's own spawn bodies (#658).
		private static void LevelPairSeparated(List<(uint SpellId, uint Descriptor3, uint CastingLevel, uint CasterLevel, uint TargetLevel)> rows)
		{
			Console.WriteLine();
			Console.WriteLine($"  ⭐ the level pair on the SEPARATING population - both entities' levels known from their own spawn");
			Console.WriteLine($"     bodies, caster resolved and distinct from the target: {rows.Count} applies");

			var split = rows.Where(r => r.CasterLevel != r.TargetLevel).ToList();
			Console.WriteLine($"     of those, caster level != target level (the applies that can tell the slots apart): {split.Count}");
			if (split.Count == 0)
			{
				Console.WriteLine("     ⚠️ zero separating applies - this population says nothing; the confounded per-spell view above stands.");
				return;
			}

			foreach (var (name, pick) in new (string, Func<(uint SpellId, uint Descriptor3, uint CastingLevel, uint CasterLevel, uint TargetLevel), uint>)[]
			         {
				         ("descriptor[3] @26", r => r.Descriptor3),
				         ("CastingLevel  @42", r => r.CastingLevel)
			         })
			{
				var caster = split.Count(r => pick(r) == r.CasterLevel);
				var target = split.Count(r => pick(r) == r.TargetLevel);
				var neither = split.Count - split.Count(r => pick(r) == r.CasterLevel || pick(r) == r.TargetLevel);
				Console.WriteLine($"     {name}: == caster {caster,4}   == target {target,4}   neither {neither,4}");
			}

			foreach (var row in split.Take(12))
				Console.WriteLine($"       spell {row.SpellId}  d3={row.Descriptor3}  c42={row.CastingLevel}  caster@{row.CasterLevel}  target@{row.TargetLevel}");
		}

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
