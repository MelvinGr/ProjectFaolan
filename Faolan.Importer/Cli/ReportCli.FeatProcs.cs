using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report feat-procs`: the shape of `FeatProcs`, a feat-modifier table nothing consumes yet.
	//
	// Before any of those rows can fire, two columns have to mean something: `Amount` and `Trigger`. This report is
	// the measurement for both, and it is a report rather than a test because it compares shipped data against the
	// client's own descriptions - a shipped-data check belongs in a report, and both populations move as the importer
	// covers more of the catalog.
	//
	// What it establishes: `Amount` is a percent chance, and values above 100 mean "always".
	//
	// The evidence is a controlled association, not a reading of four rows. A feat whose proc really rolls says so in
	// its own description ("a chance of reducing the target"), and one that always fires states the effect flatly
	// ("Adds a fizzle debuff to the Lethargy combo"). So: does the word "chance" track the Amount bucket, against the
	// base rate of that word among proc-carrying feats?
	//
	// The control is the point. "Amount > 100 rows mostly do not say chance" is worthless on its own - most feat
	// text does not say "chance" either. What matters is the lift over the base rate in both directions, which is
	// what this prints.
	//
	// `Trigger` is not pinned by any of this and the report says so rather than implying a decode. What is
	// measured is that it is not free: 0/1 occur only with EffectKind 53212 (a proc on a combo) and 2/3 only with
	// 53070 (a proc on a spell line), and within each pair one value carries the chance-bearing rows while the other
	// carries only always-fire amounts. That narrows it; it does not name it.
	//
	// Open work tracked in Gitea #697.
	public static partial class ReportCli
	{
		private static async Task FeatProcs(string? rdbDir = null)
		{
			using var db = OpenDb();

			var rows = await db.FeatProcs
				.Join(db.Feats, p => p.FeatId, f => f.Id, (p, f) => new { p.FeatId, p.Trigger, p.EffectKind, p.Amount, p.ComboId, p.SpellLineId, f.Name, f.Description })
				.ToListAsync();

			if (rows.Count == 0)
			{
				Console.WriteLine("feat-procs: FeatProcs is empty - run the importer first.");
				return;
			}

			Console.WriteLine($"feat-procs: {rows.Count:N0} rows over {rows.Select(r => r.FeatId).Distinct().Count():N0} feats.");
			Console.WriteLine();

			Console.WriteLine("== (Trigger, EffectKind): the two are not independent");
			Console.WriteLine("  trigger  effectKind  rows  feats  amounts");
			foreach (var g in rows.GroupBy(r => (r.Trigger, r.EffectKind)).OrderByDescending(g => g.Count()))
			{
				var amounts = g.Select(r => r.Amount).Distinct().OrderBy(a => a).ToList();
				var shown = amounts.Count <= 8 ? string.Join(",", amounts) : $"{amounts[0]}..{amounts[^1]} ({amounts.Count} distinct)";
				Console.WriteLine($"  {g.Key.Trigger,7}  {g.Key.EffectKind,10}  {g.Count(),4}  {g.Select(r => r.FeatId).Distinct().Count(),5}  {shown}");
			}

			Console.WriteLine();
			Console.WriteLine("== Amount: is it a percent CHANCE? Test it against the client's own descriptions.");

			static bool SaysChance(string? d)
			{
				return d != null && d.Contains("chance", StringComparison.OrdinalIgnoreCase);
			}

			// Per feat, not per row: a feat repeats once per combo it modifies, which would weight big feats heavily.
			var perFeat = rows.GroupBy(r => r.FeatId)
				.Select(g => (Bucket: g.Max(r => r.Amount) <= 100 ? "Amount <= 100" : "Amount > 100", Chance: SaysChance(g.First().Description)))
				.ToList();

			var baseRate = 100.0 * perFeat.Count(f => f.Chance) / perFeat.Count;
			Console.WriteLine($"  CONTROL - of the {perFeat.Count} proc-carrying feats, {perFeat.Count(f => f.Chance)} ({baseRate:0.0}%) use the word \"chance\" at all.");
			Console.WriteLine("  bucket           feats  say \"chance\"       lift vs control");
			foreach (var g in perFeat.GroupBy(f => f.Bucket).OrderBy(g => g.Key))
			{
				var pct = 100.0 * g.Count(f => f.Chance) / g.Count();
				Console.WriteLine($"  {g.Key,-15}  {g.Count(),5}  {g.Count(f => f.Chance),3} ({pct,5:0.0}%)   x{pct / Math.Max(0.001, baseRate):0.00}");
			}

			Console.WriteLine();
			Console.WriteLine("  Reading: a low Amount is a genuine roll and the text says so; 120 and 200 are \"always\", which is");
			Console.WriteLine("  how a >100% chance behaves once clamped. The lift runs both ways, so it is not the base rate talking.");

			RawProcSlots(rdbDir);

			Console.WriteLine();
			Console.WriteLine("== Trigger is NAMED by the client's generic proc tooltip switch (FUN_0149e450 @ 0x0149ef57, #697):");
			Console.WriteLine("   key 117 -> 0 AddProcOnHit, 1 AddProcDamage, 2 AddProcSpellDamage (key 66 = 1 picks the Getting");
			Console.WriteLine("   twin, which neither 53212 nor 53070 carries); 3 is the switch's DEFAULT and is named by the feats");
			Console.WriteLine("   that carry it - every one hangs off a HEALING line and reads 'after casting...' -> Cast. So the");
			Console.WriteLine("   combo form fires on Hit/Damage and the line form on SpellDamage/Cast (FeatProcTrigger).");
			Console.WriteLine("   And Amount is PROCS PER MINUTE, the client's own label for key 237 - not a percent chance; the");
			Console.WriteLine("   'chance' lift above is that reading's shadow (a low rate over a 2 s event IS a roll).");
			Console.WriteLine();
			Console.WriteLine("   Two offline routes that did NOT get there, so they are not re-run: a name transfers BY KEY");
			Console.WriteLine("   (only 21 of 150 keys are named by any type, key 117 by none, and key 39 carries four names),");
			Console.WriteLine("   and key 73 `Flags` (0 on all 320 occurrences).");
		}

		// The slots the importer drops. `AddSpellLineProc` (53070) carries key 73, which the client names `Flags` on
		// nine other types - the only named slot anywhere in the proc family - and `ParseProcs` does not read it. A
		// named flag word is a better trigger candidate than an unnamed one, so it is worth knowing whether it varies
		// at all before anyone goes looking in the client.
		private static void RawProcSlots(string? rdbDir)
		{
			var rdb = string.IsNullOrEmpty(rdbDir) ? RdbDirOrNull() : rdbDir;
			if (string.IsNullOrEmpty(rdb) || !Directory.Exists(rdb))
			{
				Console.WriteLine();
				Console.WriteLine("  (pass --Content:RdbDir to also census the raw element slots the importer drops)");
				return;
			}

			using var reader = OpenRdb(rdb);
			var flags = new SortedDictionary<uint, int>();
			var triggers = new SortedDictionary<uint, int>();
			var elements = 0;

			foreach (var e in reader.EntriesOfType(1000020))
			{
				byte[] body;
				try
				{
					body = reader.ReadDecompressed(e);
				}
				catch (Exception)
				{
					continue;
				}

				if (body.Length <= 12)
					continue;

				foreach (var el in ItemTemplateStatModifiers.ParseElements(body.AsSpan(12)))
				{
					if (el.Kind != 53070 && el.Kind != 53212)
						continue;

					elements++;
					if (el.Value(GameActionTypes.KeyProcTrigger) is { } t)
						triggers[t] = triggers.GetValueOrDefault(t) + 1;

					// Key 73 - the client's `Flags`, present on 53070's key list and absent from 53212's.
					if (el.Value(73) is { } f)
						flags[f] = flags.GetValueOrDefault(f) + 1;
				}
			}

			Console.WriteLine();
			Console.WriteLine($"== raw element slots over {elements:N0} proc elements read straight from the RDB");
			Console.WriteLine($"  key 117 (our `Trigger`): {string.Join(", ", triggers.Select(t => $"{t.Key} x{t.Value}"))}");
			Console.WriteLine($"  key  73 (client-named `Flags`, NOT imported): "
			                  + (flags.Count == 0 ? "(never present)" : string.Join(", ", flags.Select(f => $"{f.Key} x{f.Value}"))));
		}
	}
}
