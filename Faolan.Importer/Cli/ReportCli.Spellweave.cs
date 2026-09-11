using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Rdb;
using Faolan.Importer.Rdb;

namespace Faolan.Importer.Cli
{
	// `--report spellweave`: the whole spellweaving ruleset, derived from the client's own spell records rather
	// than from a capture.
	//
	// The componentIndex -> effect table is per-class and no capture shows a weave firing with known components, but
	// the table is not capture-borne at all: the client declares it, in the one place a value search could never
	// reach - as the argument list of a `StartSpellWeavingMode` (53190) GameAction element, whose five keys are
	// `NanoTemplate, Arg1..Arg4` (docs/re/gameaction-keylists.tsv). Finding it needs a search by element kind,
	// which is what `--report spell-refs --report-arg kind:53190` added.
	//
	// The shape it recovers, per caster class:
	//
	//   * a `Spellweaving (1)..(6)` LADDER - one buff per stack level. Adding a weave component advances the
	//     player one rung. Each rung states the class's five weave elements (identically, so the picker survives
	//     the advance), a `Modify BaseSpellDamage` step, a `Modify NaturalEnergyRegeneration` of -15000, and two
	//     per-rung payload spells named `Beneficial Effect` / `Detrimental Effect`;
	//   * a dispatcher (`Spellweaving`, duration 0) that every rung casts. Its own record is a descending
	//     `CastNano` chain gated on `MustHavePassiveAbilityRunning <the rung below>` - the ordinary rank-table
	//     walk, used here to move the stack up by one;
	//   * the BACKLASH. Each rung's `Detrimental Effect` record is a six-way partition of one roll: the first
	//     branch reads `Rnd` (stat 520), which draws the number, and every later branch compares `LastRnd`
	//     (stat 522) against a band of it. The bands WIDEN as the stack climbs, so the odds of escaping a
	//     backlash fall rung by rung - which is the escalating risk the mechanic is described by.
	//
	// `Spellweave Gone Bad: Weak/Moderate/Strong/Maximum` (196612-196615) is not the backlash. Those four sit
	// beside `test Spellweave: Fire 1` in a low id band and are referenced by zero spell records; the live
	// backlash is the six-spell partition above. Recorded here so the names stop being a lead.
	public static partial class ReportCli
	{
		private const uint StartSpellWeavingModeKind = 53190;
		private const uint SpellLineStatModifierKind = 53062;
		private const uint StatBaseSpellDamage = 1041;
		private const uint StatNaturalEnergyRegeneration = 1351;
		private const uint StatRnd = 520;
		private const uint StatLastRnd = 522;

		private static void Spellweave(string rdbDir)
		{
			using var rdb = OpenRdb(rdbDir);

			var names = new Dictionary<uint, string>();
			var elementsById = new Dictionary<uint, IReadOnlyList<ItemTemplateStatModifiers.Element>>();
			var carriers = new List<(uint Id, uint[] Elements)>();

			foreach (var entry in rdb.EntriesOfType(SpellTemplateType).Where(rdb.CanRead))
			{
				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(entry);
				}
				catch
				{
					continue;
				}

				if (body.Length < 48 || BinaryPrimitives.ReadUInt32LittleEndian(body) != SpellTemplateType)
					continue;

				var elements = ItemTemplateStatModifiers.ParseElements(body.AsSpan(12));
				elementsById[entry.Id] = elements;
				names[entry.Id] = RdbFeatImporter.ReadNameAndDescription(body).Name;

				foreach (var element in elements.Where(e => e.Kind == StartSpellWeavingModeKind && e.Values.Length >= 5))
					carriers.Add((entry.Id, element.Values.Take(5).ToArray()));
			}

			string Name(uint id)
			{
				return names.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n) ? n : "?";
			}

			IReadOnlyList<ItemTemplateStatModifiers.Element> Elements(uint id)
			{
				return elementsById.TryGetValue(id, out var e) ? e : [];
			}

			Console.WriteLine($"spellweaving: {carriers.Count} StartSpellWeavingMode ({StartSpellWeavingModeKind}) carriers "
			                  + $"over {elementsById.Count:N0} RDB-{SpellTemplateType} records");

			var profiles = carriers
				.GroupBy(c => string.Join(",", c.Elements))
				.OrderBy(g => g.Min(c => c.Id))
				.ToList();

			Console.WriteLine($"{profiles.Count} distinct element list(s) - one per weaving class.");

			foreach (var profile in profiles)
			{
				var elementIds = profile.First().Elements;

				// The ladder in stack order. The rung is stated by the record's own `Modify BaseSpellDamage` step -
				// the ids are not reliably ordered (two of the four classes run their band DESCENDING, and two are
				// interleaved with unrelated records), which is exactly why a base+offset model of the stack is
				// wrong for them. The top rung carries the whole 6..25 continuation, so its first step is its rung.
				var rungs = profile
					.Select(c => (c.Id, Step: FirstModify(Elements(c.Id), StatBaseSpellDamage)))
					.OrderBy(r => r.Step)
					.ToList();

				var dispatchers = rungs
					.Select(r => CastTargets(Elements(r.Id)).ToHashSet())
					.Aggregate((a, b) => a.Intersect(b).ToHashSet());

				// The dispatcher is the shared cast target that casts the BOTTOM rung back - the chain's entry point.
				var bottom = rungs[0].Id;
				var dispatcher = dispatchers.FirstOrDefault(d => CastTargets(Elements(d)).Contains(bottom));

				// The class, stated mechanically rather than inferred from the element names. Every rung carries
				// `SpellLineStatModifier` elements against the class's own spell lines, and a spell line is
				// `ConanClass * 1000 + index` (docs/subsystems/combos.md) - so the line's leading block is the
				// class id. It agrees with the element names on all four (Hell/Hand of Doom -> 44 Demonologist,
				// Mitra's Grace -> 24 Priest of Mitra,...), which is what makes the naming safe to rely on.
				// Not every leading block is a class - the archetype-shared combos famously sit under a `20`
				// block that is not Guardian (docs/subsystems/combos.md), and the Demonologist ladder carries a
				// `50000` line that resolves to no class at all. So the blocks are split here rather than
				// averaged: the ones that are real classes name the profile, the rest are printed as what they
				// are - unresolved - instead of being quietly folded in.
				var blocks = profile
					.SelectMany(c => Elements(c.Id).Where(e => e.Kind == SpellLineStatModifierKind && e.Values.Length >= 1))
					.Select(e => e.Values[0] / 1000)
					.Distinct()
					.OrderBy(v => v)
					.ToList();

				var classIds = blocks.Where(b => Enum.IsDefined(typeof(ConanClass), (ConanClass)b)).ToList();
				var unresolved = blocks.Except(classIds).ToList();

				Console.WriteLine();
				Console.WriteLine($"=== profile: {string.Join(" / ", elementIds.Skip(2).Select(Name))} ===");
				Console.WriteLine($"  class: {string.Join(", ", classIds.Select(c => $"{(ConanClass)c} ({c} / 0x{c:X2})"))}"
				                  + " - from the SpellLineStatModifier lines (ConanClass * 1000 + index)"
				                  + (unresolved.Count > 0 ? $"; line blocks naming no class: {string.Join(", ", unresolved)}" : ""));
				Console.WriteLine("  elements (the componentIndex indexes this list, in order):");
				for (var i = 0; i < elementIds.Length; i++)
					Console.WriteLine($"    [{i}] {elementIds[i]} '{Name(elementIds[i])}'");

				Console.WriteLine($"  dispatcher: {dispatcher} '{Name(dispatcher)}'"
				                  + (dispatcher == 0 ? "  (NOT FOUND - the shared-cast intersection names none)" : ""));
				Console.WriteLine("  ladder:");
				foreach (var (id, step) in rungs)
				{
					var elements = Elements(id);
					var regen = FirstModify(elements, StatNaturalEnergyRegeneration);
					var payloads = CastTargets(elements).Where(t => !dispatchers.Contains(t)).ToList();
					Console.WriteLine($"    {id} '{Name(id)}'  BaseSpellDamage +{step}  "
					                  + $"NaturalEnergyRegeneration {(int)regen}  payload {string.Join("/", payloads.Select(p => $"{p} '{Name(p)}'"))}");
				}

				Console.WriteLine("  backlash (one roll, partitioned; the band a rung leaves UNCOVERED is its escape chance):");
				foreach (var (id, _) in rungs)
				{
					foreach (var payload in CastTargets(Elements(id)).Where(t => !dispatchers.Contains(t)))
					{
						var bands = Backlash(Elements(payload));
						if (bands.Count == 0)
							continue;

						var covered = bands.Sum(b => b.High - b.Low + 1);
						Console.WriteLine($"    {Name(id)} -> {payload} '{Name(payload)}': "
						                  + string.Join(" | ", bands.Select(b => $"{b.Low}-{b.High} {b.Spell} {Name(b.Spell)}"))
						                  + $" | escape {100 - covered}%");
					}
				}
			}
		}

		/// <summary>The first <c>Modify</c> element's amount for the given stat, or 0 when the record states none.</summary>
		private static uint FirstModify(IReadOnlyList<ItemTemplateStatModifiers.Element> elements, uint stat)
		{
			foreach (var element in elements)
			{
				if (element.Kind != ModifyKind || element.Values.Length < 2 || element.Values[0] != stat)
					continue;

				return element.Values[1];
			}

			return 0;
		}

		private static IEnumerable<uint> CastTargets(IReadOnlyList<ItemTemplateStatModifiers.Element> elements)
		{
			return elements.Where(e => e.Kind == CastNanoKind && e.Values.Length >= 1).Select(e => e.Values[0]).Distinct();
		}

		/// <summary>
		///     A <c>Detrimental Effect</c> record read as the backlash partition: every <c>CastNano</c> whose criteria
		///     compare <see cref="StatRnd" /> or <see cref="StatLastRnd" />, as an inclusive band of the one roll.
		///     <c>Less v</c> bounds the band above at <c>v - 1</c>, <c>Larger v</c> below at <c>v + 1</c>.
		/// </summary>
		private static List<(uint Spell, int Low, int High)> Backlash(IReadOnlyList<ItemTemplateStatModifiers.Element> elements)
		{
			var bands = new List<(uint Spell, int Low, int High)>();

			foreach (var element in elements)
			{
				if (element.Kind != CastNanoKind || element.Values.Length < 1)
					continue;

				int low = 0, high = 99;
				var isRoll = false;
				foreach (var criterion in element.Criteria)
				{
					if (criterion.Id != StatRnd && criterion.Id != StatLastRnd)
						continue;

					isRoll = true;
					switch ((CriteriaOperator)criterion.Operator)
					{
						case CriteriaOperator.Less:
							high = Math.Min(high, (int)criterion.Value - 1);
							break;
						case CriteriaOperator.Larger:
							low = Math.Max(low, (int)criterion.Value + 1);
							break;
					}
				}

				if (isRoll)
					bands.Add((element.Values[0], low, high));
			}

			return bands.OrderBy(b => b.Low).ToList();
		}
	}
}
