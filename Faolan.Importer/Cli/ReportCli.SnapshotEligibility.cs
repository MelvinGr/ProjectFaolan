using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report snapshot-eligibility`: which quests can ride the login snapshot, and which arm stops the rest.
	//
	// A held quest that fails `SnapshotTails.IsSnapshotEligible` is simply absent from the journal - there is no live
	// restore path, so this number is a gameplay bound rather than a curiosity. The ceiling is not the @609 objective
	// region: the region gate and the record scalars both sit downstream of the quest's five header text refs, and the
	// scarcest of those is the summary (slot 1, LDB category 50302).
	//
	// Why that is a hard bound and not a backlog item. A text ref is `(Category, RefId, key)` and the first two
	// are derivable - the summary's category is always 50302 and its `RefId` is the quest's own id on all of them. The
	// `key` is not: it is a per-string 128-bit TEA key that retail's own database holds and the server sends on the
	// wire, it is uniformly random (7.998 bits/byte), it is absent from the client, and ten candidate derivations from
	// `(RefId, Category)` score zero. So a quest whose summary key no capture carried cannot have its text rendered by
	// the client, and that is what caps this report's bottom line. See `Faolan.Core/Crypt/LdbKey.cs`.
	//
	// It is a report rather than a test for the standing reason: it measures shipped data against what the
	// corpus happens to carry, and both move as captures land. A test would pin a number that is supposed to climb.
	//
	// Exit code is 0 always - a measurement.
	public static partial class ReportCli
	{
		// Mirrors SnapshotTails.HeaderSlots, which is private to the codec layer (as ImportOps does).
		private static readonly QuestTextSlot[] EligibilityHeaderSlots =
			[QuestTextSlot.Title, QuestTextSlot.Summary, QuestTextSlot.QuestLineTitle, QuestTextSlot.RegionName, QuestTextSlot.MapName];

		private static async Task<int> SnapshotEligibility()
		{
			await using var db = OpenDb();

			var lines = await db.QuestLines.AsNoTracking().ToDictionaryAsync(l => l.QuestLineId);
			var quests = await db.QuestDefinitions.AsNoTracking()
				.Include(q => q.Goals)
				.Include(q => q.TextRefs)
				.ToListAsync();
			var lineSteps = quests.Where(q => q.QuestLineId != 0).GroupBy(q => q.QuestLineId).ToDictionary(g => g.Key, g => g.Count());
			int StepsOf(QuestDefinition q) => lineSteps.GetValueOrDefault(q.QuestLineId);

			Console.WriteLine($"snapshot eligibility over {quests.Count} quest definitions\n");

			// Each arm INDEPENDENTLY - "how many quests would pass if this were the only requirement" - because the
			// cumulative funnel's order decides which arm gets the blame, and the question here is which arm is
			// scarcest, not which one happens to be tested first.
			var arms = new (string Name, Func<QuestDefinition, bool> Holds)[]
			{
				("has a goal", q => q.Goals.Count > 0),
				("line carries its 4CC (Magic)", q => lines.TryGetValue(q.QuestLineId, out var l) && l.Magic != 0),
				("has a Level", q => q.Level != 0),
				("@609 region verified or buildable", q => q.ObjectiveRegionVerified || SnapshotTails.IsObjectiveRegionBuildable(q)),
				("Quest_c record scalars", q => q.HasSnapshotRecord),
				// The index block states the held quest's step number; a multi-step line with no captured order
				// cannot state one, so its quests wait for a capture that orders the line.
				("line ordered (sequence, or 1 step)", q => StepsOf(q) <= 1 || q.JournalSequence != 0)
			};

			Console.WriteLine($"  {"arm",-38} {"passes",8}");
			Console.WriteLine($"  {new string('-', 38)} {new string('-', 8)}");
			foreach (var (name, holds) in arms)
				Console.WriteLine($"  {name,-38} {quests.Count(holds),8}");

			// The header slots broken out one by one: this is the finding, and it only shows up per slot.
			foreach (var slot in EligibilityHeaderSlots)
			{
				var n = quests.Count(q => q.TextRefs.Any(t => t.Slot == slot));
				var keyed = quests.Count(q => q.TextRefs.Any(t => t.Slot == slot && !t.KeyText.IsEmpty));
				Console.WriteLine($"  {$"header text: {slot}",-38} {n,8}   ({keyed} with an LDB key)");
			}

			var eligible = quests.Count(q => SnapshotTails.IsSnapshotEligible(q, lines.GetValueOrDefault(q.QuestLineId), StepsOf(q)));
			Console.WriteLine($"\n  ELIGIBLE (every arm)                   {eligible,8}");

			// The diagnostic that actually names a culprit: for each arm, how many quests fail that ARM and nothing
			// ELSE. A count of "quests failing arm X" blames every arm equally when the arms all fail together, which
			// is exactly what happens here - every capture-only input is known for the same 114 quests.
			var all = new List<(string Name, Func<QuestDefinition, bool> Holds)>(arms);
			foreach (var slot in EligibilityHeaderSlots)
			{
				var s = slot;
				all.Add(($"header text: {s}", q => q.TextRefs.Any(t => t.Slot == s)));
			}

			Console.WriteLine($"\n  {"fails ONLY this arm",-38} {"quests",8}");
			Console.WriteLine($"  {new string('-', 38)} {new string('-', 8)}");
			foreach (var (name, holds) in all)
			{
				var only = quests.Count(q => !holds(q) && all.All(other => other.Name == name || other.Holds(q)));
				Console.WriteLine($"  {name,-38} {only,8}");
			}

			Console.WriteLine("\n  ⚠️ A row of zeroes here is the finding, not a bug: the capture-only inputs - the SUMMARY's");
			Console.WriteLine("     per-string LDB key, the Quest_c record's Level word, and every goal's wire KIND tag - are all");
			Console.WriteLine("     known for the SAME quests, because one captured journal carries all three at once. So no single");
			Console.WriteLine("     arm can be relaxed to widen coverage; what widens it is a capture. See #389.");
			Console.WriteLine("     The one arm that DOES fail alone is the line ORDER (#926): a quest with everything else whose");
			Console.WriteLine("     multi-step line no capture has ordered. That too is a capture - a login of a character who");
			Console.WriteLine("     completed the line, whose completed journal carries the [seq][questId] pairs.");
			Console.WriteLine("\n  The summary's category (50302) and RefId (the quest's own id) ARE derivable; its 128-bit LDB key");
			Console.WriteLine("  is not - uniformly random, absent from the client, and not a function of (RefId, Category). Without");
			Console.WriteLine("  it the client cannot decrypt the text. See Faolan.Core/Crypt/LdbKey.cs.");

			return 0;
		}
	}
}
