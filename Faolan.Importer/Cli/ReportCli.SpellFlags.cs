using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Rdb;
using Faolan.Importer.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report spell-flags`: sweeps the spell record's FLAGS word bit by bit against things the DB knows, so
	// "which bit says X" is a question that can be ASKED rather than argued (#632).
	//
	// The word sits five words before the property run (`RdbSpellImporter.ReadFlagsWord`), and two of its bits are
	// already named: **bit 2** is the mana-cost mode (#629/#630) and **bit 24** the hide-from-tooltip bit (#789).
	//
	// ⭐ Bit 2 is printed FIRST and deliberately, as the CONTROL. It reproduces `Spell.ManaCostIsPercent` exactly -
	// 1,778 set against 1,778, zero disagreement - which is what a bit that IS a predicate looks like. Every other
	// row is read against that yardstick: 99% is a lead, 100% is a definition. Without the control a 99% row reads
	// as an answer, which is the mistake this report exists to prevent.
	public static partial class ReportCli
	{
		// One record's flags word beside every predicate the sweep scores it against. `Duration` is the record's OWN
		// field 8 rather than the `DurationMs` column, so a disagreement cannot be a column blend (#632).
		private sealed record FlagsRow(uint Flags, bool Duration, bool Cast, bool Mana, bool ManaPercent, bool Trainable,
			bool Mount, bool Area, bool Combo, bool Proc, bool Criteria, bool Elements, bool CastNano, uint Id,
			uint RawField8, string Name);

		// ⭐ NAMED (#632): bit 16 GATES THE CLIENT'S TIMED-EFFECT PATH. `FUN_007ee710`'s bit-set branch is the only
		// place that computes a duration at all - it fetches stat 0x1D0 `DurationModifier`, scales by it and by 0.01,
		// and lands the result in a longlong time. The sweep had it as a 99.07% association with "the record carries
		// field 8"; the client turns that into a mechanism, and the mechanism explains the residual the association
		// could not: the 131 records that state a duration with the bit CLEAR store a value that is NEVER APPLIED,
		// because the code that would apply it sits behind the bit. `VisualBuffTest` heading that list is what a
		// never-applied duration looks like. See docs/rdb/spell-record-fields.md.
		private const int DurationBit = 16;

		// The spell record's own duration property (`docs/rdb/spell-record-fields.md`).
		private const uint DurationFieldId = 8;

		// The three PROC GameAction kinds (`docs/re/gameaction-type-ids.tsv`) - a record carrying one of these IS a
		// proc, which is the "is-a-proc" predicate #632 named and no DB column supplies.
		private static readonly HashSet<uint> ProcKinds = [53211, 53212, 53213];

		private static async Task SpellFlags(string rdbDir, string? arg)
		{
			using var rdb = OpenRdb(rdbDir);
			await using var db = OpenDb();

			var spells = await db.Spells.AsNoTracking()
				.Select(s => new { s.Id, s.DurationMs, s.CastTime, s.ManaCost, s.ManaCostIsPercent })
				.ToDictionaryAsync(s => s.Id, s => s);
			var trainable = (await db.ClassSpells.AsNoTracking().Select(c => c.SpellId).Distinct().ToListAsync()).ToHashSet();
			var mounts = (await db.Mounts.AsNoTracking().Select(m => m.Id).ToListAsync()).ToHashSet();
			var areas = (await db.SpellAreaEffects.AsNoTracking().Select(a => a.SpellId).Distinct().ToListAsync()).ToHashSet();
			// ⭐ The SHARPER predicates #632 asked for. The first three come from the record itself rather than from a
			// column, which is what makes them worth adding: a bit that means "this is a proc" has to agree with the
			// record CARRYING a proc element, and no DB column says that.
			var combos = (await db.Combos.AsNoTracking().Select(c => c.GameActionId).Distinct().ToListAsync()).ToHashSet();

			var rows = new List<FlagsRow>();
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

				if (record.Length < 12 || !spells.TryGetValue(entry.Id, out var spell))
					continue;

				var body = record.AsSpan(12);
				RdbSpellImporter.ReadIncreasingFields(body, out var runStart, out var runEnd);
				if (runStart < 20)
					continue;

				var elements = ItemTemplateStatModifiers.ParseElements(body);
				// ⭐ The record's OWN duration field, not the `DurationMs` column - that column also takes values from
				// the wire and the effect catalog, so a disagreement read against it could be a column blend rather
				// than the bit being wrong (#632).
				var fields = RdbSpellImporter.ReadIncreasingFields(body, out _);
				var rawDuration = fields.GetValueOrDefault(DurationFieldId);
				var (recordName, _, _) = RdbSpellImporter.ReadStringBlock(body, runEnd);
				rows.Add(new FlagsRow(RdbSpellImporter.ReadFlagsWord(body, runStart), rawDuration > 0, spell.CastTime > 0,
					spell.ManaCost > 0, spell.ManaCostIsPercent, trainable.Contains(entry.Id), mounts.Contains(entry.Id),
					areas.Contains(entry.Id), combos.Contains(entry.Id),
					elements.Any(e => ProcKinds.Contains(e.Kind)), elements.Any(e => e.Criteria.Any()),
					elements.Count > 0, elements.Any(e => e.Kind == CastNanoKind), entry.Id, rawDuration, recordName));
			}

			// A predicate is worth a column when it is neither rare nor universal - one that is true of everything
			// cannot separate any bit from any other.
			var predicates = new (string Name, Func<FlagsRow, bool> Of)[]
			{
				("duration", r => r.Duration), ("cast", r => r.Cast), ("mana", r => r.Mana),
				("mana%", r => r.ManaPercent), ("train", r => r.Trainable), ("mount", r => r.Mount), ("area", r => r.Area),
				("combo", r => r.Combo), ("proc", r => r.Proc), ("criteria", r => r.Criteria),
				("elements", r => r.Elements), ("castnano", r => r.CastNano)
			};

			Console.WriteLine($"spell FLAGS-word bit sweep over {rows.Count:N0} records (#632)");
			Console.WriteLine("  agreement = how often the BIT and the predicate say the same thing. Bit 2 is the control:");
			Console.WriteLine("  it is a known predicate (ManaCostIsPercent, #630) and it scores 100.00%. Read every other");
			Console.WriteLine("  row against that - 99% is a lead, 100% is a definition.");
			Console.WriteLine();
			Console.WriteLine("  ⚠️ A RARE bit scores high against a rare predicate for free: both are mostly false, so they");
			Console.WriteLine("  agree on every record that has neither. Read the `set` column first - a bit set on 4 records");
			Console.WriteLine("  scoring 99% against `mount` (155 records) is the base rate, not a finding.");
			Console.WriteLine();
			// ⭐ The base-rate correction the warning above only ASKED for. Two mostly-false columns agree on every
			// record that has neither, so a raw percentage rewards rarity. `expected` is what a bit of this bit's own
			// rarity would score against this predicate's own prevalence by chance alone; EXCESS is the difference,
			// and it is the only number in this table that means anything on its own.
			var prevalence = predicates.Select(p => (double)rows.Count(p.Of) / rows.Count).ToArray();
			Console.WriteLine($"  predicate prevalence: " + string.Join("  ", predicates.Select((p, i) => $"{p.Name}={100 * prevalence[i]:0.0}%")));
			Console.WriteLine();
			Console.WriteLine($"  EXCESS over chance, in points. The control (bit 2 vs mana%) is the yardstick.");
			Console.WriteLine($"  {"bit",3} {"set",7}   " + string.Join("  ", predicates.Select(p => $"{p.Name,-9}")));

			var only = uint.TryParse((arg ?? "").Trim(), out var parsed) ? parsed : (uint?)null;
			for (var bit = 0; bit < 32; bit++)
			{
				if (only is { } wanted && wanted != bit)
					continue;

				var set = rows.Count(r => (r.Flags >> bit & 1) == 1);
				if (set == 0)
					continue;

				var bitRate = (double)set / rows.Count;
				var cells = predicates.Select((p, i) =>
				{
					var agree = (double)rows.Count(r => ((r.Flags >> bit & 1) == 1) == p.Of(r)) / rows.Count;
					var expected = bitRate * prevalence[i] + (1 - bitRate) * (1 - prevalence[i]);
					var excess = 100 * (agree - expected);
					// A cell at or below chance carries no information; printing it as a number invites reading it
					// as one, so it prints as a dot.
					return excess < 1.0 ? $"{".",8}  " : $"{excess,7:+0.0;-0.0} ";
				});

				Console.WriteLine($"  {bit,3} {set,7:N0}   " + string.Join("  ", cells));
			}

			// ⭐ The strongest row, examined rather than summarised. A bit that IS a predicate has a residual you can
			// characterise; one that merely correlates has a residual that looks like the rest of the catalog.
			DurationBitResidual(rows);
		}

		// ⭐ Bit 16 against the record's own duration field, and what the disagreements ARE (#632).
		private static void DurationBitResidual(IReadOnlyList<FlagsRow> rows)
		{
			var setNoDuration = rows.Where(r => (r.Flags >> DurationBit & 1) == 1 && !r.Duration).ToList();
			var durationNotSet = rows.Where(r => (r.Flags >> DurationBit & 1) == 0 && r.Duration).ToList();

			Console.WriteLine();
			Console.WriteLine($"  ⭐ bit {DurationBit} vs the record's OWN field 8, residual broken out:");
			Console.WriteLine($"     bit set, no duration : {setNoDuration.Count,5:N0}");
			Console.WriteLine($"     duration, bit clear  : {durationNotSet.Count,5:N0}   <- the direction that would DISPROVE it");
			Console.WriteLine($"     agree                : {rows.Count - setNoDuration.Count - durationNotSet.Count,5:N0} of {rows.Count:N0}");

			// The asymmetry is the finding: a flag that means "carries a duration" may legitimately be set on a
			// record whose duration is supplied elsewhere, but it must never be CLEAR on one that states its own.
			foreach (var (label, sample) in new[] { ("bit set, no duration", setNoDuration), ("duration, bit clear", durationNotSet) })
			{
				if (sample.Count == 0)
					continue;

				Console.WriteLine($"     {label} - first few:");
				foreach (var r in sample.OrderBy(r => r.Id).Take(6))
					Console.WriteLine($"        {r.Id,-9} field8={r.RawField8,-9} {(r.Name.Length > 44 ? r.Name[..44] : r.Name)}");
			}

			// ⚠️ Does the residual cluster on the duration's VALUE? If the exceptions were "durations of a particular
			// kind" they would; if they look like the rest of the catalog, the simple reading is just wrong.
			var agreeing = rows.Where(r => (r.Flags >> DurationBit & 1) == 1 && r.Duration).Select(r => r.RawField8).ToList();
			foreach (var (label, values) in new[] { ("agreeing", agreeing), ("bit clear", durationNotSet.Select(r => r.RawField8).ToList()) })
			{
				var sorted = values.Order().ToList();
				if (sorted.Count == 0)
					continue;

				Console.WriteLine($"     field 8 on the {label,-9} set: n={sorted.Count,-6} min={sorted[0],-8} median={sorted[sorted.Count / 2],-9} max={sorted[^1]}");
			}
		}
	}
}
