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

			var rows = new List<(uint Flags, bool Duration, bool Cast, bool Mana, bool ManaPercent, bool Trainable, bool Mount, bool Area)>();
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
				RdbSpellImporter.ReadIncreasingFields(body, out var runStart, out _);
				if (runStart < 20)
					continue;

				rows.Add((RdbSpellImporter.ReadFlagsWord(body, runStart), spell.DurationMs > 0, spell.CastTime > 0,
					spell.ManaCost > 0, spell.ManaCostIsPercent, trainable.Contains(entry.Id), mounts.Contains(entry.Id),
					areas.Contains(entry.Id)));
			}

			// A predicate is worth a column when it is neither rare nor universal - one that is true of everything
			// cannot separate any bit from any other.
			var predicates = new (string Name, Func<(uint Flags, bool Duration, bool Cast, bool Mana, bool ManaPercent, bool Trainable, bool Mount, bool Area), bool> Of)[]
			{
				("duration", r => r.Duration), ("cast", r => r.Cast), ("mana", r => r.Mana),
				("mana%", r => r.ManaPercent), ("train", r => r.Trainable), ("mount", r => r.Mount), ("area", r => r.Area)
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
			Console.WriteLine($"  {"bit",3} {"set",7}   " + string.Join("  ", predicates.Select(p => $"{p.Name,-9}")));

			var only = uint.TryParse((arg ?? "").Trim(), out var parsed) ? parsed : (uint?)null;
			for (var bit = 0; bit < 32; bit++)
			{
				if (only is { } wanted && wanted != bit)
					continue;

				var set = rows.Count(r => (r.Flags >> bit & 1) == 1);
				if (set == 0)
					continue;

				var cells = predicates.Select(p =>
				{
					var agree = rows.Count(r => ((r.Flags >> bit & 1) == 1) == p.Of(r));
					return $"{100.0 * agree / rows.Count,7:0.00}% ";
				});

				Console.WriteLine($"  {bit,3} {set,7:N0}   " + string.Join("  ", cells));
			}
		}
	}
}
