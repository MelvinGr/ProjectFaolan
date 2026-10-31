using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;

namespace Faolan.Importer.Cli
{
	// `--report conversation-fold-stability`: does adding one capture to the corpus preserve the node ids the fold
	// already produced?
	//
	// The conversations module is Overwrite-only because `ConversationGraph.Fold` is a global partition over every
	// harvested walk: which screens merge, and which need a `Discriminator`, is decided by comparing the walks against
	// each other. There are two ways out of that, and the cheaper one has to be tested rather than assumed:
	//
	//   (a) persist the harvest so the fold can be re-run from the database;
	//   (b) prove the fold is INCREMENTALLY stable for the append-only case - a new walk can only split a class, never
	//       merge two - and apply just the splits.
	//
	// This is the test (b) needs. It folds the whole corpus, then folds it again with one capture held out, and asks
	// how many of the held-out run's node ids survive into the full one. A surviving id is a node an incremental
	// importer could have left alone; a LOST id is a row whose primary key moved, taking its packets, edges and
	// entries with it.
	//
	// It has to be a report and not a test: the answer is a property of the shipped corpus, which is not in this
	// repo, and it changes whenever a capture is added - exactly the shipped-data-gate rule in Faolan.Tests/AGENTS.md.
	public static partial class ReportCli
	{
		private static void ConversationFoldStability(IReadOnlyList<NamedCapture> captures)
		{
			var walksOf = captures.ToDictionary(c => c.Name, c => ConversationExtractor.Extract(c.Packets));
			var withWalks = captures.Where(c => walksOf[c.Name].Count > 0).OrderBy(c => c.Name, StringComparer.Ordinal).ToList();

			Console.WriteLine($"conversation fold stability over {captures.Count} captures, {withWalks.Count} of which carry dialogue (#643)");
			if (withWalks.Count < 2)
			{
				Console.WriteLine("  fewer than two captures carry dialogue - nothing to hold out");
				return;
			}

			var fullGraph = ConversationGraph.Fold(HarvestOf(withWalks.SelectMany(c => walksOf[c.Name])));
			var full = fullGraph.Nodes.Select(n => n.Id).ToHashSet();
			// The fragility metric, and the reason the headline below cannot be read as "the fold is stable". The
			// only way an append moves an existing id is by renumbering a discriminator, and a discriminator can only
			// be renumbered where there is more than one class in a (template, contentKey) group. A corpus in which
			// every node sits at discriminator 0 has nothing to renumber - so an append-stable result there is a fact
			// about the data, not about the function.
			var discriminated = fullGraph.Nodes.Count(n => n.Discriminator > 0);
			Console.WriteLine($"  the WHOLE corpus folds to {full.Count:N0} node(s), {discriminated:N0} of them at a discriminator above 0");
			Console.WriteLine();
			Console.WriteLine("  holding each capture out in turn: how many of the smaller fold's node ids survive when it is added back");
			Console.WriteLine($"  {"held out",-62} {"nodes",7} {"kept",7} {"LOST",7}");

			var worst = 0;
			var perfect = 0;
			var rows = new List<(string Name, int Nodes, int Lost)>();
			foreach (var held in withWalks)
			{
				var without = FoldOf(withWalks.Where(c => c.Name != held.Name).SelectMany(c => walksOf[c.Name]));
				var lost = without.Count(id => !full.Contains(id));
				rows.Add((held.Name, without.Count, lost));
				worst = Math.Max(worst, lost);
				if (lost == 0)
					perfect++;
			}

			foreach (var (name, nodes, lost) in rows.OrderByDescending(r => r.Lost).ThenBy(r => r.Name, StringComparer.Ordinal).Take(FoldStabilitySampleRows))
			{
				var label = name.Length > 60 ? name[..60] : name;
				Console.WriteLine($"  {label,-62} {nodes,7:N0} {nodes - lost,7:N0} {lost,7:N0}");
			}

			if (rows.Count > FoldStabilitySampleRows)
				Console.WriteLine($"  … {rows.Count - FoldStabilitySampleRows} more, all with fewer lost ids than the rows above");

			Console.WriteLine();
			Console.WriteLine($"  captures whose addition moves NO existing node id : {perfect:N0} / {rows.Count:N0}");
			Console.WriteLine($"  worst single capture                              : {worst:N0} node id(s) moved");
			Console.WriteLine();

			// The verdict, stated rather than left to the reader - the whole point of the report is to settle (b)
			// one way or the other, and "some ids move" and "no ids move" are opposite answers to it.
			if (worst == 0)
			{
				Console.WriteLine("  ⭐ Every capture is append-stable: adding one moves no id the fold had already assigned.");
				Console.WriteLine("     So #643's option (b) holds ON THIS CORPUS, and an incremental importer would only ever ADD rows.");
				Console.WriteLine();
				Console.WriteLine("  ⚠️ And WHY it holds is the caveat, not a footnote. A node's id is");
				Console.WriteLine("     ComputeId(template, contentKey, DISCRIMINATOR), and the discriminator is the class's INDEX within");
				Console.WriteLine("     its (template, contentKey) group, ordered by each class's own first walk. So the only way an");
				Console.WriteLine("     append can move an existing id is by RENUMBERING a group that holds more than one class - a new");
				Console.WriteLine("     walk that splits a class renumbers every class the new one sorts before, and a new walk that is");
				Console.WriteLine("     the earliest in its group renumbers all of them.");
				Console.WriteLine($"     This corpus has {discriminated:N0} node(s) above discriminator 0, so there is almost nothing to renumber.");
				Console.WriteLine("     That makes the result a fact about the DATA rather than about the fold. An incremental importer");
				Console.WriteLine("     built on it wants this report in its acceptance path, or a discriminator that is a function of the");
				Console.WriteLine("     CLASS rather than of its position - which removes the failure mode outright.");
				return;
			}

			Console.WriteLine("  ⛔ #643's option (b) is FALSE: adding a capture moves node ids that already existed.");
			Console.WriteLine("     A node's id is ComputeId(template, contentKey, DISCRIMINATOR), and the discriminator is the class's");
			Console.WriteLine("     INDEX within its (template, contentKey) group, ordered by each class's own first walk. A new walk that");
			Console.WriteLine("     splits one class therefore renumbers every class the new one sorts before - and a new walk that is the");
			Console.WriteLine("     earliest in its group renumbers the whole group. Splits are local; the NUMBERING is not.");
			Console.WriteLine("     So an incremental import needs either (a) - re-fold from a persisted harvest - or a discriminator that");
			Console.WriteLine("     is a function of the CLASS rather than of its position.");
		}

		// The fold's node ids for a set of walks, through exactly the path the importer uses: extract, split the
		// variants, merge, fold. Anything less would measure a different function than the one the module runs.
		//
		// `NpcTemplateId` is not filtered against the database here, where the importer filters against imported
		// templates. That only ever REMOVES walks, uniformly in both runs, so it cannot change whether an id moves -
		// and keeping the DB out of it makes the report a pure function of the corpus.
		private static HashSet<uint> FoldOf(IEnumerable<ConversationWalk> walks)
		{
			return ConversationGraph.Fold(HarvestOf(walks)).Nodes.Select(n => n.Id).ToHashSet();
		}

		private static List<ConversationGraph.Harvested> HarvestOf(IEnumerable<ConversationWalk> walks)
		{
			var split = ConversationVariants.SplitConflicts(walks.ToList(), []);
			return ConversationExtractor.Merge(split)
				.Where(e => e.NpcTemplateId is not null)
				.Select(e => new ConversationGraph.Harvested(e, []))
				.ToList();
		}

		// How many held-out rows to print. The rest are strictly better by construction (the table is sorted by lost
		// ids), and the two summary lines below carry the answer regardless - so the cap hides nothing.
		private const int FoldStabilitySampleRows = 15;
	}
}
