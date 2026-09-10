using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report quest-line-order` (#926): does ANY shipped column order the steps of a quest line?
	//
	// 1,009 quest lines carry no journal ordering at all - every member sits at `JournalSequence = 0`, because no
	// capture ever put a character on them. The question this answers is whether the order can be recovered from
	// something we already hold, and it is answered by SCORING each candidate against the 770 lines whose captured
	// sequence is complete: a column that orders a line will reproduce that sequence.
	//
	// ⭐ **Nothing does, and the quest ID beats every column that was supposed to.** Ascending template id gets
	// 89.4%; `Level`, `BandLevel` and the three undecoded `Field*` words all score BELOW it, i.e. each one adds noise
	// to an ordering the id already carries. So the ticket's "the undecoded QuestLine columns have not been read
	// against this question" is now closed negative rather than open.
	//
	// ⚠️ And 89.4% is not a rule. It decays with line length exactly as authoring order would - 93.5% on two-step
	// lines, where it is barely better than a coin flip's worth of evidence, down to 50% on the six-step-and-longer
	// lines that are the only place a real order shows. Quest ids are assigned roughly in authoring order and
	// authoring order usually matches play order; that is a correlation, not the client stating anything.
	//
	// ⛔ Two of the ticket's candidates need no scoring at all: `Magic` and the `Trailer*` group are columns of
	// `QuestLine`, one row per LINE, so they cannot express an order WITHIN one. `Quest.tier` / `start_new_tier`, the
	// fields shaped for exactly this, are empty in the client's catalog (`--report rdb-quest-catalog`).
	//
	// ⭐ What retail actually states is the quest INDEX block's ordered id list - and we DERIVE that from
	// `JournalSequence` rather than the reverse (`QuestIndexRegions.ForQuests`), so it is a consumer of the order,
	// not a source of it. Which is why this ticket is capture coverage and not decode.
	public static partial class ReportCli
	{
		private static async Task QuestLineOrder()
		{
			Console.WriteLine("=== does any shipped column order a quest line's steps? (#926) ===\n");

			List<Candidate> quests;
			await using (var db = OpenDb())
			{
				quests = await db.QuestDefinitions.AsNoTracking()
					.Where(q => q.QuestLineId != 0)
					.Select(q => new Candidate(q.QuestLineId, q.QuestId, q.JournalSequence, q.Level, q.BandLevel, q.FieldEc, q.FieldF0, q.FieldF8))
					.ToListAsync();
			}

			var byLine = quests.GroupBy(q => q.LineId).ToDictionary(g => g.Key, g => g.ToList());

			// Only lines whose captured sequence is COMPLETE can score anything: a line with a hole cannot say whether
			// a candidate got it right, and a line of one is moot.
			var scorable = byLine.Values
				.Where(l => l.Count > 1 && l.All(q => q.Sequence > 0) && l.Select(q => q.Sequence).Order().SequenceEqual(Enumerable.Range(1, l.Count).Select(i => (uint)i)))
				.ToList();

			Console.WriteLine($"  {byLine.Count} line(s) shipped, {byLine.Values.Count(l => l.Count > 1)} of them multi-step, "
			                  + $"{scorable.Count} with a COMPLETE captured sequence to score against.");
			Console.WriteLine($"  {byLine.Values.Count(l => l.Count > 1 && l.All(q => q.Sequence == 0))} multi-step line(s) carry no ordering at all - the population this ticket is about.\n");

			Console.WriteLine($"  {"candidate",-32} {"lines ordered right",20}   by line length");
			Score(scorable, q => ((long)q.QuestId, 0L), "ascending quest id");
			Score(scorable, q => ((long)q.Level, q.QuestId), "Level, then id");
			Score(scorable, q => ((long)q.BandLevel, q.QuestId), "BandLevel, then id");
			Score(scorable, q => ((long)q.FieldEc, q.QuestId), "FieldEc, then id");
			Score(scorable, q => ((long)q.FieldF0, q.QuestId), "FieldF0, then id");
			Score(scorable, q => ((long)q.FieldF8, q.QuestId), "FieldF8, then id");
			Score(scorable, q => (-(long)q.QuestId, 0L), "DESCENDING id (control)");

			Console.WriteLine();
			Console.WriteLine("  ⭐ The id beats every column that was supposed to order a line, and each of those scores BELOW it -");
			Console.WriteLine("     they add noise to an ordering the id already carries. None of them is the mechanism.");
			Console.WriteLine("  ⚠️ Nor is the id: 89% that decays to ~50% on the long lines is authoring-order correlation. Quest ids");
			Console.WriteLine("     are assigned roughly in the order the content was written, and that usually matches play order.");
			Console.WriteLine("  ⛔ `Magic` and the `Trailer*` group need no score - they are QuestLine columns, one row per LINE, so");
			Console.WriteLine("     they cannot express an order WITHIN one.");
		}

		private static void Score(IReadOnlyList<List<Candidate>> lines, Func<Candidate, (long, long)> key, string name)
		{
			var ok = 0;
			var byLength = new Dictionary<int, (int Ok, int Total)>();
			foreach (var line in lines)
			{
				var want = line.OrderBy(q => q.Sequence).Select(q => q.QuestId).ToArray();
				var got = line.OrderBy(key).Select(q => q.QuestId).ToArray();
				var hit = want.SequenceEqual(got);
				if (hit)
					ok++;

				var bucket = Math.Min(line.Count, 6);
				var (bOk, bTotal) = byLength.GetValueOrDefault(bucket);
				byLength[bucket] = (bOk + (hit ? 1 : 0), bTotal + 1);
			}

			var spread = string.Join("  ", byLength.OrderBy(b => b.Key).Select(b => $"{b.Key}{(b.Key == 6 ? "+" : "")}:{b.Value.Ok}/{b.Value.Total}"));
			Console.WriteLine($"  {name,-32} {$"{ok}/{lines.Count} ({100.0 * ok / lines.Count:F1}%)",20}   {spread}");
		}

		private sealed record Candidate(uint LineId, uint QuestId, uint Sequence, uint Level, uint BandLevel, uint FieldEc, uint FieldF0, ulong FieldF8);
	}
}
