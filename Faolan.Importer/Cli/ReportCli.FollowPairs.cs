using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;

namespace Faolan.Importer.Cli
{
	// `--report follow-pairs` - every captured NPC-follows-NPC binding, with the formation offsets each follower
	// held (#741).
	//
	// It exists as a REPORT rather than a scratch script because the useful output is a survey that moves with the
	// corpus: which creature types are bound to which, how many INSTANCES of each pair a capture carries (the
	// formation is only visible as a group), and which bindings name a leader we cannot resolve - the residue that
	// says whether the harvest is complete.
	public static partial class ReportCli
	{
		private static void FollowPairs(IReadOnlyList<NamedCapture> captures)
		{
			var perCapture = new List<(string Capture, List<NpcFollowPair> Pairs)>();
			foreach (var capture in captures)
			{
				var pairs = NpcFollowPairExtractor.Extract(capture.Packets);
				if (pairs.Count > 0)
					perCapture.Add((capture.Name, pairs));
			}

			var all = perCapture.SelectMany(c => c.Pairs).ToList();
			Console.WriteLine($"follow-pairs: {all.Count} formation-follow record(s) over {perCapture.Count} capture(s), "
			                  + $"{all.Select(p => (p.Follower, p.Leader)).Distinct().Count()} distinct (follower -> leader) pair(s).");
			Console.WriteLine();

			Console.WriteLine("== by creature pair");
			foreach (var group in all.GroupBy(p => (p.Follower, p.Leader)).OrderByDescending(g => g.Count()))
			{
				var offsets = group.Select(p => $"({p.OffsetX:0.##},{p.OffsetY:0.##},{p.OffsetZ:0.##})#{p.Slot}").Distinct().OrderBy(o => o).ToList();
				Console.WriteLine($"  {group.Key.Follower,-34} follows {group.Key.Leader,-34} x{group.Count(),-4} offsets: {string.Join(" ", offsets)}");
			}

			Console.WriteLine();
			Console.WriteLine("== what the merge would store (one leader per follower)");
			foreach (var pair in NpcFollowPairExtractor.Merge(all).OrderBy(p => p.Follower))
				Console.WriteLine($"  {pair.Follower,-34} -> {pair.Leader}");

			Console.WriteLine();
			Console.WriteLine("== per capture");
			foreach (var (capture, pairs) in perCapture)
			{
				Console.WriteLine($"  {capture}");
				foreach (var group in pairs.GroupBy(p => (p.Follower, p.Leader)).OrderByDescending(g => g.Count()))
					Console.WriteLine($"     {group.Key.Follower,-30} follows {group.Key.Leader,-30} x{group.Count()}");
			}
		}
	}
}
