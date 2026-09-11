using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Importer.Captures;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report buff-duration-check`: does every captured buff apply agree with the `Spells.DurationMs` we would
	// build it from?
	//
	// Retail's apply carries the duration in centiseconds at payload offset 8; our builder takes it from the spell
	// row. Three outcomes, and only one of them is a bug:
	//
	//   captured == row      the builder would reproduce the apply.
	//   captured <  row      a RESTORE: a relog re-applies a running buff carrying the time LEFT, not the base
	//                        duration. Expected, and reported as a population rather than a failure.
	//   captured >  row      an apply cannot outlast the spell it is built from, so the row is wrong. split in
	//                        two, because only one half is a defect: a row still at the empty placeholder
	//                        (`DurationMs <= 10`) is one `CaptureSpellImporter.ImportBuffDurationsAsync`
	//                        deliberately declines - it adopts a wire duration only when two distinct captures state
	//                        it, so a single sighting leaves the placeholder standing. That is the rule working, and
	//                        failing on it would turn the check red the moment a new spell is seen once. A row with a
	//                        real but shorter duration has no such excuse.
	//
	// Why it is a report and not a test, the same argument as `--report npc-pose-check` and
	// `--report item-level-check`: it compares importer output (`Spells.DurationMs`) against importer input (the
	// captured applies). As a test it read the input through `fixtures.bin`'s corpus half - a frozen, derived slice -
	// and PINNED the three populations as exact counts (2,696 exact / 233 short / 41 of those under an hour). Both
	// sides of that comparison move when captures are added: a newer dump can correct a `DurationMs`, and new applies
	// enter the dumps but not the frozen extract. So the pins were a guaranteed rebaseline on every data change, on a
	// gate whose real content is the one assertion that something is wrong.
	//
	// Run against the real dumps there is one population, the two sides are always in step, and the populations are
	// printed instead of pinned - a number that moves when data is added is a fact about the data, not a regression.
	// What is left asserted is the direction that can only be a defect.
	//
	// What stays in `MagicBuffCorpusTests` is everything that is a property of the code or of the wire alone - the
	// 54-byte payload shape, the zero words, descriptor[2] echoing the spell id, the byte-exact rebuild. Those need
	// no DB and cannot desync.
	public static partial class ReportCli
	{
		// The apply payload's duration slot, in centiseconds (MagicBuffData's layout: Counter, CounterType, Duration).
		private const int BuffDurationOffset = 8;

		// The apply form's low byte (1 is the end twin, 2 an UPDATE), and its fixed payload width.
		private const byte BuffApplyForm = 0;
		private const int BuffApplyPayloadLength = 54;

		// The "no duration known" placeholder a spell row carries until something sources one - the same value
		// `CaptureSpellImporter.EmptyDurationMs` gates its adoption on.
		private const uint EmptyBuffDurationMs = 10;

		// Below this the shortfall reads as the restore path rather than as a decode gap: a buff of an hour or more
		// is the kind a relog catches mid-run.
		private const uint BuffRestoreThresholdCs = 360000;

		private static async Task<int> BuffDurationCheck(IReadOnlyList<NamedCapture> captures)
		{
			var applies = new List<(string Capture, MagicBuffIIR Buff)>();
			foreach (var capture in captures)
			{
				foreach (var p in capture.Packets)
				{
					if (p is not { IsIirCarrier: true, IsDownload: true, SubOpcode: MagicBuffCodec.Opcode })
						continue;

					if (!p.TryParse(MagicBuffCodec.Parse, out var buff))
						continue;
					if (buff is not { Form: BuffApplyForm, Data.Length: BuffApplyPayloadLength })
						continue;

					applies.Add((capture.Name, buff));
				}
			}

			await using var db = OpenDb();
			var ids = applies.Select(a => a.Buff.SpellBaseId).Distinct().ToList();
			var durations = await db.Spells.AsNoTracking().Where(s => ids.Contains(s.Id))
				.ToDictionaryAsync(s => s.Id, s => s.DurationMs);

			int compared = 0, exact = 0;
			var shortfall = new List<(uint Spell, uint Captured, uint Row)>();
			var over = new List<string>();
			var unsourced = new List<string>();

			foreach (var (capture, buff) in applies)
			{
				if (!durations.TryGetValue(buff.SpellBaseId, out var durationMs))
					continue; // no row to construct from - not a builder failure

				compared++;
				var captured = buff.Data.ReadU32Be(BuffDurationOffset);
				var expected = durationMs / 10;
				if (captured == expected)
					exact++;
				else if (captured < expected)
					shortfall.Add((buff.SpellBaseId, captured, expected));
				else if (durationMs <= EmptyBuffDurationMs)
					unsourced.Add($"spell 0x{buff.SpellBaseId:X8}: retail states {captured} cs, row is the empty placeholder ({durationMs} ms)");
				else
					over.Add($"{capture} spell 0x{buff.SpellBaseId:X8}: captured {captured} cs > row {expected} cs");
			}

			var shortAndBrief = shortfall.Count(s => s.Row < BuffRestoreThresholdCs);
			Console.WriteLine($"buff-duration-check over {captures.Count} capture(s): {applies.Count} captured apply/applies, "
				+ $"{compared} resolved to a Spells row.");
			Console.WriteLine($"  exact match to the row : {exact}");
			Console.WriteLine($"  SHORTFALL (a restore)  : {shortfall.Count}   of which on buffs under "
				+ $"{BuffRestoreThresholdCs / 100}s: {shortAndBrief}   (the restore reading does not explain those - #658)");

			if (compared == 0)
			{
				Console.WriteLine("  ⚠️ nothing resolved to a Spells row - the check is vacuous, so this is a failure, not a pass.");
				return 1;
			}

			if (unsourced.Count > 0)
			{
				Console.WriteLine($"  {unsourced.Distinct().Count()} spell(s) whose row is still the EMPTY placeholder while retail states a duration -");
				Console.WriteLine("    the two-distinct-captures rule declining a single sighting, not a defect:");
				foreach (var line in unsourced.Distinct().Take(10))
					Console.WriteLine($"      {line}");
			}

			if (over.Count == 0)
			{
				Console.WriteLine("  ✅ no apply outlasts a row that states a real duration.");
				return 0;
			}

			Console.WriteLine($"  ⚠️ {over.Count} apply/applies whose duration EXCEEDS the Spells row - a wrong Spells.DurationMs:");
			foreach (var line in over.Take(20))
				Console.WriteLine($"    {line}");

			return 1;
		}
	}
}
