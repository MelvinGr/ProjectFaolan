using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report rank-tables [--report-arg <spell id>]`: how a rank TABLE picks WHICH of its members applies (#861).
	//
	// A rank table is a spell in NO stacking line carrying one `CastNano` edge per rank of ONE line - `Furious
	// Inspiration`'s 299074 fans out to the ten ranks of line 44235. #813 established that exactly one member may
	// apply (a stacking line is a mutual-exclusion group) and left WHICH as an open question, answered by the walk's
	// arrival order, and said so: "that is a pick, not a decode".
	//
	// ⭐⭐ It IS a decode, and this report is the sweep that shows it. Every edge carries a CRITERION run, and the
	// operator that matters is `MustHavePassiveAbilityRunning` (91), whose operand is a SPELL. Two shapes:
	//
	//   * an UPGRADE LADDER - the gate names another member of the SAME table, specifically the rank below, and the
	//     edges are stored highest-rank-first. Read in order, first match wins: nothing running -> rank 1, rank 5
	//     running -> rank 6. ⭐ The TOP edge additionally ORs in ITSELF, so a subject at the cap refreshes rather
	//     than falling back to rank 1 - an asymmetry with no other possible meaning.
	//   * a TRAINED-RANK LOOKUP - the gate names a spell OUTSIDE the table, the passive rank the character actually
	//     holds (`Rampaging Horde`'s ten edges each require a different rank of the trained ability). ⚠️ Note this is
	//     the rule the ticket ruled out for line 44235 and it is right for these four - the criterion, not a global
	//     rule, is what says which mechanism a table uses.
	//
	// The residue is honest and printed: a table with NO gate at all really does offer the walk nothing to choose
	// with, and there arrival order remains the incumbent.
	//
	// ⭐ Why a report and not a test: both populations are shipped RDB data (#729). The CODE property - that reading
	// the run in order produces a one-rank-per-proc ladder - is gated in `RankTableLadderTests`.
	public static partial class ReportCli
	{
		private const uint MustHavePassiveAbilityRunning = (uint)CriteriaOperator.MustHavePassiveAbilityRunning;
		private const uint CastNanoLinkKind = 53051;

		private static async Task<int> RankTables(string? spellFilter)
		{
			await using var db = OpenDb();

			var names = await db.Spells.AsNoTracking().Select(s => new { s.Id, s.Name, s.GuiStackingLine }).ToDictionaryAsync(s => s.Id, s => s);
			var links = await db.SpellAreaEffects.AsNoTracking().Where(a => a.Kind == CastNanoLinkKind)
				.Select(a => new { a.SpellId, a.Sequence, a.EffectSpellId }).ToListAsync();
			var gates = (await db.SpellAreaEffectCriteria.AsNoTracking()
					.Where(c => c.Operator == MustHavePassiveAbilityRunning)
					.Select(c => new { c.SpellId, c.Sequence, c.Value, c.Pos })
					.ToListAsync())
				.GroupBy(c => (c.SpellId, c.Sequence))
				.ToDictionary(g => g.Key, g => g.OrderBy(c => c.Pos).Select(c => c.Value).ToArray());

			if (gates.Count == 0)
			{
				Console.WriteLine("rank-tables: SpellAreaEffectCriteria is empty - run `--only rdb-spells` first (#861 added the table).");
				return 1;
			}

			// A rank TABLE: the carrier is in no line, and every edge lands in ONE line that is not 0.
			var tables = links.GroupBy(l => l.SpellId)
				.Where(g => g.Count() >= 3
				            && (names.GetValueOrDefault(g.Key)?.GuiStackingLine ?? 0) == 0
				            && g.Select(l => names.GetValueOrDefault(l.EffectSpellId)?.GuiStackingLine ?? 0).Distinct().Count() == 1
				            && (names.GetValueOrDefault(g.First().EffectSpellId)?.GuiStackingLine ?? 0) != 0)
				.ToDictionary(g => g.Key, g => g.OrderBy(l => l.Sequence).ToArray());

			var shapes = new Dictionary<string, List<uint>>(StringComparer.Ordinal);
			foreach (var (carrier, edges) in tables)
			{
				var members = edges.Select(e => e.EffectSpellId).ToHashSet();
				var all = edges.SelectMany(e => gates.GetValueOrDefault((e.SpellId, e.Sequence), [])).ToList();
				var internalGates = all.Count(g => members.Contains(g));

				var shape = all.Count == 0 ? "no gate at all - arrival order is all there is"
					: internalGates == all.Count ? "UPGRADE LADDER (every gate names another member)"
					: internalGates == 0 ? "TRAINED-RANK LOOKUP (every gate names a spell outside the table)"
					: "mixed";
				(shapes.TryGetValue(shape, out var list) ? list : shapes[shape] = []).Add(carrier);
			}

			Console.WriteLine($"rank-tables: {tables.Count} rank table(s) over {tables.Sum(t => t.Value.Length)} edges.");
			foreach (var (shape, carriers) in shapes.OrderByDescending(s => s.Value.Count))
				Console.WriteLine($"  {carriers.Count,3} x {shape}");

			// ⭐ The ladder's defining asymmetry, counted rather than asserted from one example: only the TOP edge
			// gates on ITSELF, and that is what makes a subject at the cap refresh instead of dropping to rank 1.
			var selfGating = tables.Count(t => gates.GetValueOrDefault((t.Key, t.Value[0].Sequence), []).Contains(t.Value[0].EffectSpellId));
			Console.WriteLine($"  {selfGating,3} x table whose FIRST edge gates on ITSELF (the refresh-at-the-cap rung)");

			var ladders = shapes.GetValueOrDefault("UPGRADE LADDER (every gate names another member)", []);
			var stepsDown = ladders.Count(carrier =>
			{
				var edges = tables[carrier];
				return Enumerable.Range(0, edges.Length - 1)
					.All(i => gates.GetValueOrDefault((carrier, edges[i].Sequence), []).Contains(edges[i + 1].EffectSpellId));
			});
			Console.WriteLine($"  {stepsDown,3} of {ladders.Count} ladders gate EVERY edge on the NEXT one in the record - i.e. the record's order IS the ladder");

			if (string.IsNullOrWhiteSpace(spellFilter) || !uint.TryParse(spellFilter, out var wanted) || !tables.TryGetValue(wanted, out var detail))
				return 0;

			Console.WriteLine();
			Console.WriteLine($"detail for {wanted} ({names.GetValueOrDefault(wanted)?.Name ?? "?"}):");
			foreach (var edge in detail)
			{
				var run = gates.GetValueOrDefault((edge.SpellId, edge.Sequence), []);
				var requires = run.Length == 0
					? "(no gate - always permitted)"
					: "requires " + string.Join(" OR ", run.Select(g => $"{g} '{names.GetValueOrDefault(g)?.Name ?? "?"}'"));
				Console.WriteLine($"  seq {edge.Sequence,2} -> {edge.EffectSpellId,-9} {names.GetValueOrDefault(edge.EffectSpellId)?.Name ?? "?",-28} {requires}");
			}

			return 0;
		}
	}
}
