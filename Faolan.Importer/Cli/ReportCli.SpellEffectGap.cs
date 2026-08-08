using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Rdb;
using Faolan.GameServer.Combat;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report spell-effect-gap [--report-arg <kind or stat id>]`: the imported-vs-APPLIED census of
	// `SpellStatEffects` (#701) - how many of the shipped rows reach a live engine, and for the rest, which of the
	// two gates stopped them.
	//
	// It exists because that gap was counted BY HAND once and the number outlived the code by a wide margin. #701 was
	// filed on "2,015 rows dropped by KIND across 48 kinds", with the conclusion that a general spell-effect
	// dispatcher was the missing piece. By the time anyone read it the by-key element fix (#591/#701) and the pool
	// engine (#702) had reshaped the table underneath it: 15 kinds, not 48, and the kind gate had stopped being where
	// the rows go. A hand-census cannot notice that; this asks the ENGINES, so it cannot say anything they do not.
	//
	// Db-sourced - no captures, no RDB - so it answers from whatever `faolan.db` a clone actually has.
	public static partial class ReportCli
	{
		private static async Task SpellEffectGap(string? arg)
		{
			await using var db = OpenDb();
			var rows = await db.SpellStatEffects.AsNoTracking().ToListAsync();
			var names = await db.Spells.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name);

			// A row lands if EITHER engine takes it: the pool engine owns the instant half (a heal), the stat engine
			// the lasting half (+62 Hit Rating). Ask the pool first - its kinds are ones the stat engine reports as
			// unhandled, so the other order would file every heal as a drop.
			var verdicts = rows
				.Select(r => (Row: r, Pool: SpellPoolEffects.Applies(r), Stat: BuffStatEffects.Classify(r)))
				.ToList();

			if (!string.IsNullOrWhiteSpace(arg) && uint.TryParse(arg, out var only))
			{
				Detail(verdicts, names, only);
				return;
			}

			var pool = verdicts.Count(v => v.Pool);
			var flat = verdicts.Count(v => v is { Pool: false, Stat: BuffStatEffects.Gate.Flat });
			var percent = verdicts.Count(v => v is { Pool: false, Stat: BuffStatEffects.Gate.Percent });
			var byKind = verdicts.Where(v => v is { Pool: false, Stat: BuffStatEffects.Gate.UnhandledKind }).ToList();
			var byStat = verdicts
				.Where(v => v is { Pool: false, Stat: BuffStatEffects.Gate.UnmodelledStat or BuffStatEffects.Gate.PercentOnUnmodelledStat })
				.ToList();

			Console.WriteLine($"SpellStatEffects: {rows.Count} imported rows over {rows.Select(r => r.SpellId).Distinct().Count()} spells");
			Console.WriteLine();
			Console.WriteLine("  APPLIED");
			Console.WriteLine($"    lasting stat delta  (BuffStatEffects)  {flat + percent,6}   flat {flat} / percent {percent}");
			Console.WriteLine($"    instant pool move   (SpellPoolEffects) {pool,6}");
			Console.WriteLine("  DROPPED");
			Console.WriteLine($"    not a stat delta at all (the KIND gate) {byKind.Count,5}");
			Console.WriteLine($"    a stat delta on an unmodelled stat      {byStat.Count,5}");
			Console.WriteLine();

			// The KIND table's load-bearing column is the last one. A kind's ROW COUNT says how much of the table it
			// is; how many of those rows land on a stat the engine ALREADY models says what admitting the kind would
			// be worth today - and those two numbers have almost nothing to do with each other, which is the whole
			// reason #701's ranking by count pointed at the wrong work.
			Console.WriteLine("  dropped by KIND - the record is not a stat delta (a flag write, a spell modifier, a particle effect)");
			Console.WriteLine($"    {"kind",-6} {"name",-28} {"rows",5} {"spells",6}  on an already-modelled stat");
			foreach (var g in byKind.GroupBy(v => v.Row.EffectKind).OrderByDescending(g => g.Count()))
			{
				var modelled = g.Count(v => BuffStatEffects.Models(v.Row.StatId));
				Console.WriteLine($"    {g.Key,-6} {GameActionTypes.Get(g.Key)?.Name ?? "?",-28} {g.Count(),5} {g.Select(v => v.Row.SpellId).Distinct().Count(),6}  {modelled}");
			}

			Console.WriteLine();
			Console.WriteLine("  dropped by STAT - a stat delta `gear + sum(buffs)` cannot express, or whose stat is unmodelled");
			Console.WriteLine($"    {"stat",-6} {"name",-34} {"rows",5} {"spells",6}  kinds");
			foreach (var g in byStat.GroupBy(v => v.Row.StatId).OrderByDescending(g => g.Count()).Take(25))
			{
				var kinds = string.Join(" ", g.Select(v => v.Row.EffectKind).Distinct().OrderBy(k => k));
				Console.WriteLine($"    {(uint)g.Key,-6} {Name(g.Key),-34} {g.Count(),5} {g.Select(v => v.Row.SpellId).Distinct().Count(),6}  {kinds}");
			}

			Console.WriteLine();
			Console.WriteLine("  --report-arg <kind or stat id> lists the rows behind one line.");
		}

		// One line's rows, with the spell that carries them - the form that turns a count into something you can read
		// a description off. The argument is matched against BOTH id spaces because they do not overlap in practice
		// (kinds are ~53000, stats are under 1400) and making the caller say which would be ceremony.
		private static void Detail(
			List<(SpellStatEffect Row, bool Pool, BuffStatEffects.Gate Stat)> verdicts,
			IReadOnlyDictionary<uint, string> names,
			uint id)
		{
			var hits = verdicts
				.Where(v => v.Row.EffectKind == id || (uint)v.Row.StatId == id)
				.OrderBy(v => (uint)v.Row.StatId)
				.ThenBy(v => v.Row.Amount)
				.ToList();

			if (hits.Count == 0)
			{
				Console.WriteLine($"no rows carry kind or stat {id}.");
				return;
			}

			Console.WriteLine($"{hits.Count} row(s) for {id} ({GameActionTypes.Get(id)?.Name ?? Name((CharacterStatId)id)})");
			Console.WriteLine($"  {"kind",-6} {"stat",-6} {"name",-30} {"amount",10} {"ramp",-22} {"verdict",-24} spell");
			foreach (var (row, applied, gate) in hits)
			{
				var ramp = row is { HighAmount: { } high, LowLevel: { } low, HighLevel: { } top }
					? $"-> {high} over lvl {low}..{top}"
					: "";
				var verdict = applied
					? "APPLIED (pool)"
					: gate is BuffStatEffects.Gate.Flat or BuffStatEffects.Gate.Percent
						? $"APPLIED ({gate})"
						: gate.ToString();
				Console.WriteLine(
					$"  {row.EffectKind,-6} {(uint)row.StatId,-6} {Name(row.StatId),-30} {row.Amount,10} {ramp,-22} {verdict,-24} " +
					$"{names.GetValueOrDefault(row.SpellId, "?")} ({row.SpellId})");
			}
		}

		// The client's own name for a stat where `CharacterStatId` has a member for it, the bare id where it does not.
		// An unnamed id is worth seeing as a number rather than hidden behind a placeholder - it is the signal that a
		// family nobody has looked at is sitting in the table.
		private static string Name(CharacterStatId stat)
		{
			var name = stat.ToString();
			return name == ((uint)stat).ToString() ? $"({name})" : name;
		}
	}
}
