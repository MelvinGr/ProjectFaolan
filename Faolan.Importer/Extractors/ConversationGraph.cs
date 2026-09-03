using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Extractors
{
	// Folds the harvested WALKS into the dialogue GRAPH the runtime serves (#642).
	//
	// A capture is a walk: the player opened a window and clicked their way down one route, so the extractor produces
	// one row per (route position, quest state). That is the right shape to HARVEST and the wrong shape to STORE -
	// keyed by the answer-index path it cannot represent a loop, it stores every convergent screen once per route that
	// reaches it, and an answer INDEX means different things in different variants of the same screen.
	//
	// This turns those walks into what retail actually has: screens named by their CONTENT, and explicit edges named by
	// the clicked option's own LDB string id.
	//
	//   1. Signature each walk by the ordered `category:id` list of its remoteformat screens.
	//   2. Partition-refine within each signature group: two same-content walks MERGE unless a SHARED (option, gate)
	//      slot leads to children in different classes. Shared-only is what lets a truncated loop lap merge into the
	//      fuller screen instead of splitting off; iterate to a fixed point (terminates, and handles cycles).
	//   3. Emit one node per class, with a Discriminator assigned deterministically within the signature group.
	//   4. Emit the edges and entries from the walks' parent/child links.
	//
	// The four irreducible splits in the shipped corpus are real - retail reuses the same line and option strings at
	// two different script states and the follow-ups differ (Tina 2683 and 12413, Laranga 19827, Turach 2822).
	public static class ConversationGraph
	{
		// One harvested walk with its packets already columnized - the fold reads the option ids off the AnswerList's
		// screen rows, so it needs the decoded form rather than the raw bytes.
		public sealed record Harvested(ConversationWalk Walk, List<ConversationPacket> Packets);

		public sealed record Node(uint Id, string NpcName, uint NpcTemplateId, string ContentKey, uint Discriminator, List<ConversationPacket> Packets);

		public sealed record Edge(uint FromNodeId, uint OptionId, uint ToNodeId, uint? ConditionQuestId, QuestState? ConditionQuestState, uint? GrantQuestId);

		public sealed record Entry(string NpcName, uint NpcTemplateId, ConversationTrigger Trigger, uint? TriggerWorldObjectId, uint? ConditionQuestId, QuestState? ConditionQuestState,
			bool? RequiresRepeatGreeting, uint NodeId);

		public sealed record Graph(List<Node> Nodes, List<Edge> Edges, List<Entry> Entries, List<string> Report);

		// The fixed point is reached in a handful of rounds on the shipped corpus; the cap is a runaway guard, not a
		// budget - refinement is monotone (classes only ever split), so it cannot oscillate.
		private const int MaxRefinementRounds = 32;

		public static Graph Fold(IReadOnlyList<Harvested> harvested)
		{
			ArgumentNullException.ThrowIfNull(harvested);

			var report = new List<string>();

			// #646: a walk whose NPC could not be attributed to a TEMPLATE is dropped here, before anything is folded.
			// The template is the screen's identity now, so an unattributed walk has no key - and the runtime routes a
			// click by template, so it could never be reached even if it were stored. Reported per NPC rather than
			// silently skipped: a template that stops resolving is a harvest regression, not a shrug.
			foreach (var group in harvested.Where(h => h.Walk.NpcTemplateId == null).GroupBy(h => h.Walk.NpcName).OrderBy(g => g.Key, StringComparer.Ordinal))
			{
				report.Add($"{(string.IsNullOrEmpty(group.Key) ? $"#{group.First().Walk.CapturedNpcHandle}" : group.Key)}: "
				           + $"{group.Count()} walk(s) resolve to no NPC template - dropped (a screen with no template has no identity)");
			}

			// A deterministic order everything downstream inherits: the fold must be a pure function of the corpus, or
			// two runs over the same captures produce different Discriminators and faolan.sql churns for nothing.
			var walks = harvested.Where(h => h.Walk.NpcTemplateId != null).OrderBy(WalkKey, StringComparer.Ordinal).ToList();
			var contentKeys = walks.ToDictionary(h => h, ContentKeyOf);
			var options = walks.ToDictionary(h => h, OptionIdsOf);
			var parents = LinkParents(walks, contentKeys, report);

			// --- 2. partition refinement -------------------------------------------------------------------------
			var classOf = walks.ToDictionary(h => h, h => $"{h.Walk.NpcTemplateId}\0{contentKeys[h]}");
			for (var round = 0; round < MaxRefinementRounds; round++)
			{
				var refined = Refine(walks, classOf, parents, options);
				if (refined.All(kv => classOf[kv.Key] == kv.Value))
					break;

				classOf = refined;
			}

			// --- 3. one node per class ---------------------------------------------------------------------------
			var nodes = new List<Node>();
			var nodeIdOf = new Dictionary<string, uint>();
			foreach (var group in walks.GroupBy(h => $"{h.Walk.NpcTemplateId}\0{contentKeys[h]}").OrderBy(g => g.Key, StringComparer.Ordinal))
			{
				// Ordered by each class's own first walk, so the Discriminator is a function of the corpus rather than
				// of the order the refinement happened to converge in.
				var classes = group.GroupBy(h => classOf[h]).OrderBy(c => WalkKey(c.OrderBy(WalkKey, StringComparer.Ordinal).First()), StringComparer.Ordinal).ToList();
				if (classes.Count > 1)
					report.Add($"{group.First().Walk.NpcName}: {classes.Count} distinct screens share the content {contentKeys[group.First()]} - discriminated");

				for (var i = 0; i < classes.Count; i++)
				{
					var members = classes[i].ToList();
					// The RICHEST capture of the screen wins its packet list, the same rule Merge applies between
					// captures of one walk: a truncated dump must never displace a full one.
					var best = members.OrderByDescending(m => m.Packets.Count).ThenBy(WalkKey, StringComparer.Ordinal).First();
					var id = ConversationNode.ComputeId(best.Walk.NpcTemplateId!.Value, contentKeys[best], (uint)i);
					nodeIdOf[classes[i].Key] = id;
					nodes.Add(new Node(id, best.Walk.NpcName, best.Walk.NpcTemplateId.Value, contentKeys[best], (uint)i, best.Packets));
				}
			}

			// --- 4. edges and entries ----------------------------------------------------------------------------
			var edges = new Dictionary<(uint From, uint Option, uint? Quest, QuestState? State), Edge>();
			var entries = new Dictionary<(uint, ConversationTrigger, uint?, uint?, QuestState?, bool?), Entry>();

			foreach (var h in walks)
			{
				var toId = nodeIdOf[classOf[h]];
				if (h.Walk.AnswerPath.Length == 0)
				{
					var trigger = TriggerOf(h.Walk);
					var key = (h.Walk.NpcTemplateId!.Value, trigger, h.Walk.TriggerWorldObjectId, h.Walk.ConditionQuestId, h.Walk.ConditionQuestState, h.Walk.RequiresRepeatGreeting);
					if (!entries.TryAdd(key, new Entry(h.Walk.NpcName, h.Walk.NpcTemplateId.Value, trigger, h.Walk.TriggerWorldObjectId, h.Walk.ConditionQuestId, h.Walk.ConditionQuestState,
							h.Walk.RequiresRepeatGreeting, toId))
						&& entries[key].NodeId != toId)
						report.Add($"{h.Walk.NpcName}: two different screens open the same {trigger} root under the same gate - kept the first");

					continue;
				}

				if (!parents.TryGetValue(h, out var link))
					continue; // already reported by LinkParents

				var fromId = nodeIdOf[classOf[link.Parent]];
				var (quest, state) = EdgeGate(link.Parent.Walk, h.Walk);
				var slot = (fromId, link.OptionId, quest, state);
				if (edges.TryGetValue(slot, out var existing))
				{
					if (existing.ToNodeId != toId)
						report.Add($"{h.Walk.NpcName}: option {link.OptionId} out of screen 0x{fromId:X8} leads to two different screens under the same gate - kept the first");
					else if (existing.GrantQuestId == null && h.Walk.GrantQuestId != 0)
						edges[slot] = existing with { GrantQuestId = h.Walk.GrantQuestId };

					continue;
				}

				edges[slot] = new Edge(fromId, link.OptionId, toId, quest, state, h.Walk.GrantQuestId == 0
					? null
					: h.Walk.GrantQuestId);
			}

			var graph = new Graph(nodes, [.. edges.Values], [.. entries.Values], report);
			Verify(graph, walks, classOf, nodeIdOf, parents);
			return graph;
		}

		// The gate that proves the fold lost nothing: every harvested walk must still be REACHABLE, by starting at the
		// root its own capture opened on and clicking the same sequence of OPTION IDS it recorded.
		//
		// The route is rebuilt from the harvested parent links rather than from the answer path, for the same reason
		// the links themselves are harvested: a path only says "one pick shallower", and where a screen has quest-gated
		// variants that is several different screens.
		//
		// It is not a tautology over the edges just built. Merging can send a hop somewhere else - two screens that
		// looked alike folding into one is exactly the mistake this catches - and the edge dedup can drop a branch when
		// two routes disagree about where the same option leads.
		//
		// ⚠️ A hop carries its OWN gate, not the leaf walk's (#644). The route is a chain of harvested (parent, child)
		// pairs and `EdgeGate` is evaluated per pair, so the gate on hop 1 is generally a different quest from the gate
		// on hop 4 - a route can cross an ungated screen on the way to a gated one, and vice versa. Replaying with the
		// LEAF's gate at every hop was the original bug behind the three unreplayable walks: at Turach's root, option
		// 2142 offers an unconditional branch and one gated on 12407 Completed, and the walk below it is gated on 10320
		// Active, so leaf-gate matching fell through to the unconditional branch and the rest of the route did not
		// exist there. Laranga is the mirror image - a 10320-Completed leaf hanging off three ungated hops, where the
		// leaf's gate matched a SIBLING branch at hop 1 and sent the replay down the wrong sub-tree.
		//
		// Also reports the screens nothing reaches. Under the answer-path key an unreachable screen was not even
		// expressible (the path WAS the route), so this is a class of defect that only becomes visible here.
		private static void Verify(Graph graph, List<Harvested> walks, Dictionary<Harvested, string> classOf, Dictionary<string, uint> nodeIdOf,
			Dictionary<Harvested, (Harvested Parent, uint OptionId)> parents)
		{
			var out_ = graph.Edges.ToLookup(e => e.FromNodeId);
			var roots = graph.Entries.Select(e => e.NodeId).ToHashSet();
			var replayed = 0;
			var lost = 0;

			foreach (var h in walks)
			{
				// Climb to the root the capture opened on, collecting each click AND the gate that click was harvested
				// under - the same (option, gate) slot the fold keyed the edge by.
				var route = new List<(uint Option, uint? Quest, QuestState? State)>();
				var at = h;
				var broken = false;
				while (at.Walk.AnswerPath.Length != 0)
				{
					if (!parents.TryGetValue(at, out var link) || route.Count > 64)
					{
						broken = true;
						break;
					}

					var (quest, state) = EdgeGate(link.Parent.Walk, at.Walk);
					route.Insert(0, (link.OptionId, quest, state));
					at = link.Parent;
				}

				if (broken || !roots.Contains(nodeIdOf[classOf[at]]))
					continue; // no captured root, or a branch LinkParents has already reported as dropped

				var node = nodeIdOf[classOf[at]];
				var ok = true;
				foreach (var (option, quest, state) in route)
				{
					var branches = out_[node].Where(e => e.OptionId == option).ToList();
					// The hop's own slot first. The fallbacks below it are the runtime's precedence (ConversationService
					// .PickConversationEdge): a walk whose exact slot the dedup dropped still counts as replayed if the
					// branch a player would actually take lands in the same place.
					var next = branches.FirstOrDefault(e => e.ConditionQuestId == quest && e.ConditionQuestState == state)
						?? branches.FirstOrDefault(e => e.ConditionQuestId == null)
						?? branches.FirstOrDefault();
					if (next == null)
					{
						ok = false;
						break;
					}

					node = next.ToNodeId;
				}

				if (ok && node == nodeIdOf[classOf[h]])
				{
					replayed++;
				}
				else
				{
					// Named, not just counted: a walk that no longer replays is a branch the fold moved or lost, and the
					// route it took is the whole diagnosis. Bounded so a systematic break reports a symptom rather than
					// 500 lines of one.
					lost++;
					if (lost <= 10)
						graph.Report.Add($"  unreplayable: {h.Walk.NpcName} '{h.Walk.AnswerPath}' via [{string.Join(">", route.Select(r => r.Option))}] reached 0x{node:X8}, not 0x{nodeIdOf[classOf[h]]:X8}");
				}
			}

			graph.Report.Add($"walk replay: {replayed} of {walks.Count} harvested walks reach their own screen through the graph"
			                 + (lost > 0 ? $"; {lost} DO NOT" : ""));

			var reachable = graph.Entries.Select(e => e.NodeId).Concat(graph.Edges.Select(e => e.ToNodeId)).ToHashSet();
			foreach (var orphan in graph.Nodes.Where(n => !reachable.Contains(n.Id)).OrderBy(n => n.NpcName, StringComparer.Ordinal))
				graph.Report.Add($"{orphan.NpcName}: screen {orphan.ContentKey} has no root and no inbound branch - unreachable in game");
		}

		// The option buttons of a FOLDED screen, in the order the client draws them.
		private static List<uint> OptionIdsOf(Node node)
		{
			var list = node.Packets
				.Where(p => p.SubOpcode == ConversationSubOpcode.KnubotAnswerList)
				.OrderBy(p => p.Sequence)
				.LastOrDefault();

			return list == null ? [] : [.. list.Screens.OrderBy(s => s.Sequence).Select(s => s.ScreenId)];
		}

		// The walk each non-root walk BRANCHED FROM, and the LDB id of the option that was clicked.
		//
		// Both come off the WIRE (ConversationWalk.ParentContentKey / OptionId): the extractor names the screen the
		// player was looking at when they picked, by content, and the option they clicked, by its own LDB id.
		//
		// ⚠️ Deliberately not derived from the answer path, which was the first attempt and is wrong. A path only says
		// "one pick shallower", and where a screen has quest-gated VARIANTS that is several different screens. Casilda's
		// `2` sub-tree is the worked example: harvested under her five-option 12373 greeting but itself ungated, so the
		// path-derived parent attached it to the three-option unconditional greeting and index 2 named "Goodbye". The
		// walk-replay gate is what caught it - 21 of 553 walks could no longer be walked to.
		//
		// The content key identifies the parent SCREEN, which may still be several walks (the same screen appears once
		// per route that reaches it); any of them will do, because they all fold into the same class. The same-gate one
		// is preferred so the refinement sees the tightest link available.
		private static Dictionary<Harvested, (Harvested Parent, uint OptionId)> LinkParents(List<Harvested> walks, Dictionary<Harvested, string> contentKeys, List<string> report)
		{
			var byContent = walks.ToLookup(h => (h.Walk.NpcTemplateId, contentKeys[h]));
			var byPath = walks.ToLookup(h => (h.Walk.NpcTemplateId, h.Walk.TypeId, h.Walk.ServerInitiated, h.Walk.TriggerWorldObjectId, h.Walk.AnswerPath));
			var links = new Dictionary<Harvested, (Harvested Parent, uint OptionId)>();
			var recovered = 0;
			var dropped = 0;

			foreach (var h in walks)
			{
				if (h.Walk.AnswerPath.Length == 0)
					continue;

				var parent = h.Walk.ParentContentKey.Length == 0
					? null
					: Pick(byContent[(h.Walk.NpcTemplateId, h.Walk.ParentContentKey)].ToList(), h, ParentPath(h));
				var optionId = h.Walk.OptionId;

				// FALLBACK to the answer path when the harvested link does not resolve - the capture that offered the
				// option lost the merge, or its answer list was never captured. Weaker (a path only says "one pick
				// shallower", which is ambiguous across gated variants of a screen) but strictly better than dropping
				// the branch and everything under it, and it is counted so the weakness is visible rather than assumed.
				if (parent == null)
				{
					var cut = h.Walk.AnswerPath.LastIndexOf('>');
					var parentPath = cut < 0 ? "" : h.Walk.AnswerPath[..cut];
					if (!uint.TryParse(cut < 0 ? h.Walk.AnswerPath : h.Walk.AnswerPath[(cut + 1)..], out var index))
						continue;

					// A path names SEVERAL walks (one per quest-gated variant of that screen), and the index has to name a
					// button on the one we pick - so a candidate that never offered that many options cannot be the
					// parent, whatever the gate says. Shortlisting on that before the gate preference is what recovers
					// Casilda's '0>0>0', whose same-path candidates include a close screen with no answer list at all
					// (#644). If none can, fall back to the whole list so the report still names the real shortfall.
					var candidates = byPath[(h.Walk.NpcTemplateId, h.Walk.TypeId, h.Walk.ServerInitiated, h.Walk.TriggerWorldObjectId, parentPath)].ToList();
					var offering = candidates.Where(c => OptionIdsOf(c).Count > index).ToList();
					parent = Pick(offering.Count > 0 ? offering : candidates, h, parentPath);
					var offered = parent == null ? [] : OptionIdsOf(parent);
					if (index >= offered.Count)
					{
						// Named rather than counted, so the branch that is actually lost can be looked up in the corpus
						// instead of standing as an anonymous "1" forever (#644).
						dropped++;
						report.Add($"  unlinkable: {h.Walk.NpcName} '{h.Walk.AnswerPath}' - parent '{parentPath}' "
						           + (parent == null ? "was never captured" : $"offers {offered.Count} option(s), so index {index} names none"));
						continue;
					}

					optionId = offered[(int)index];
					recovered++;
				}

				links[h] = (parent!, optionId); // a null parent has no offered options, so it always took the `dropped` branch above
			}

			if (recovered > 0)
				report.Add($"{recovered} branch(es) had no harvested parent screen - linked by answer path instead");
			if (dropped > 0)
				report.Add($"{dropped} branch(es) come off a screen whose option list was never captured - dropped (no option id to name them by)");

			return links;

			// The content key names the parent SCREEN, which is usually several walks - the same screen appears once per
			// route that reaches it. Preferring the walk at the child's own parent PATH keeps those routes apart, which
			// is what lets the refinement see that two same-content screens lead different places (Laranga's 19827: one
			// route's "Goodbye" goes to 20148, the other's to 1384). Then the runtime's own precedence: the parent
			// captured under this walk's gate, else the unconditional one.
			static Harvested? Pick(List<Harvested> candidates, Harvested child, string parentPath)
			{
				var sameRoute = candidates.Where(c => c.Walk.AnswerPath == parentPath).ToList();
				var shortlist = sameRoute.Count > 0 ? sameRoute : candidates;
				return shortlist.FirstOrDefault(c => c.Walk.ConditionQuestId == child.Walk.ConditionQuestId && c.Walk.ConditionQuestState == child.Walk.ConditionQuestState)
					?? shortlist.FirstOrDefault(c => c.Walk.ConditionQuestId == null)
					?? shortlist.FirstOrDefault();
			}

			static string ParentPath(Harvested h)
			{
				var cut = h.Walk.AnswerPath.LastIndexOf('>');
				return cut < 0 ? "" : h.Walk.AnswerPath[..cut];
			}
		}

		// One refinement round: split a class whenever two of its walks disagree about where a SHARED (option, gate)
		// slot leads. Shared-only is deliberate - a walk that simply stops earlier (a loop lap the capture truncated,
		// a route nobody explored past this point) says nothing about the slots it never exercised, so it merges into
		// the fuller screen instead of splitting off as a near-duplicate.
		private static Dictionary<Harvested, string> Refine(List<Harvested> walks, Dictionary<Harvested, string> classOf,
			Dictionary<Harvested, (Harvested Parent, uint OptionId)> parents, Dictionary<Harvested, List<uint>> options)
		{
			// slot map per walk: what each of ITS options leads to, in the current classification.
			var slots = walks.ToDictionary(h => h, _ => new Dictionary<(uint, uint?, QuestState?), string>());
			foreach (var (child, link) in parents)
			{
				var (quest, state) = EdgeGate(link.Parent.Walk, child.Walk);
				slots[link.Parent][(link.OptionId, quest, state)] = classOf[child];
			}

			var refined = new Dictionary<Harvested, string>();
			foreach (var group in walks.GroupBy(h => classOf[h]))
			{
				var subgroups = new List<List<Harvested>>();
				foreach (var h in group.OrderBy(WalkKey, StringComparer.Ordinal))
				{
					var fits = subgroups.FirstOrDefault(g => g.All(other => !Conflicts(slots[h], slots[other])));
					if (fits == null)
						subgroups.Add([h]);
					else
						fits.Add(h);
				}

				// Named by the subgroup's own first walk rather than by its index, so a class keeps its name across
				// rounds even when an earlier subgroup splits underneath it.
				foreach (var subgroup in subgroups)
				{
					var name = subgroups.Count == 1 ? group.Key : $"{group.Key}#{WalkKey(subgroup[0])}";
					foreach (var h in subgroup)
						refined[h] = name;
				}
			}

			return refined;
		}

		private static bool Conflicts(Dictionary<(uint, uint?, QuestState?), string> a, Dictionary<(uint, uint?, QuestState?), string> b)
		{
			return a.Any(kv => b.TryGetValue(kv.Key, out var other) && other != kv.Value);
		}

		// The gate to put ON the branch. A child gated exactly as its parent adds nothing - the player satisfied that
		// gate to be standing on the parent at all - so only a gate the parent does NOT already carry is written down.
		private static (uint? Quest, QuestState? State) EdgeGate(ConversationWalk parent, ConversationWalk child)
		{
			return child.ConditionQuestId == parent.ConditionQuestId && child.ConditionQuestState == parent.ConditionQuestState
				? (null, null)
				: (child.ConditionQuestId, child.ConditionQuestState);
		}

		private static ConversationTrigger TriggerOf(ConversationWalk walk)
		{
			if (!walk.ServerInitiated)
				return ConversationTrigger.Talk;

			return walk.TriggerWorldObjectId != null ? ConversationTrigger.UseWorldObject : ConversationTrigger.Approach;
		}

		// One screen's raw packets as the columnized rows this class reads. The same decode the conversation import
		// runs, so a content key computed here agrees with the imported graph by construction.
		//
		// A packet that will not columnize is SKIPPED rather than fatal: the callers are naming a screen for the sake of
		// a link, and ImportConversations is where a body we cannot rebuild is an error.
		internal static Harvested Columnize(List<byte[]> screen)
		{
			var packets = new List<ConversationPacket>();
			for (var i = 0; i < screen.Count; i++)
			{
				try
				{
					packets.Add(ConversationImportOps.BuildPacket(screen[i], i));
				}
				catch (Exception)
				{
				}
			}

			return new Harvested(new ConversationWalk(0, "", 0, screen, ""), packets);
		}

		// What the screen SAYS: the ordered `category:id` of every remoteformat screen it carries - the NPC's spoken
		// line (50002), each option button (50001; 0 is the universal "Goodbye") and the close screen (100:0). In the
		// shipped data that pair determines the LDB decryption key 1:1, so it is a content-derived name.
		//
		// ⚠️ A SPURIOUS trailing close is excluded, exactly as ConversationService.ServeScreen excludes it: a captured
		// screen that offers options sometimes also carries a CloseChatWindow because the capturing session shut the
		// window right after, and the runtime drops that packet so the option list stays interactive. Counting it as
		// content split Casilda's 12369 greeting off as a second screen with no branches of its own - so the root
		// served it and every pick closed the window. What the player sees is the identity; what the capture happened
		// to append after it is not.
		internal static string ContentKeyOf(Harvested h)
		{
			var interactive = h.Packets.Any(p => p.SubOpcode == ConversationSubOpcode.KnubotAnswerList);
			return string.Join(",", h.Packets
				.Where(p => !interactive || p.SubOpcode != ConversationSubOpcode.KnubotCloseChatWindow)
				.OrderBy(p => p.Sequence)
				.SelectMany(p => p.Screens.OrderBy(s => s.Sequence))
				.Select(s => $"{s.Category}:{s.ScreenId}"));
		}

		// The option buttons this screen offers, in the order the client draws them - so an answer INDEX resolves to
		// the option's own LDB id. The LAST answer list wins: a screen that carries several is one the capture
		// re-sent, and the last is what was on screen when the player clicked.
		internal static List<uint> OptionIdsOf(Harvested h)
		{
			var list = h.Packets
				.Where(p => p.SubOpcode == ConversationSubOpcode.KnubotAnswerList)
				.OrderBy(p => p.Sequence)
				.LastOrDefault();

			return list == null ? [] : [.. list.Screens.OrderBy(s => s.Sequence).Select(s => s.ScreenId)];
		}

		// A total, corpus-derived ordering of the walks - every component comes off the harvest, so it is stable
		// across runs and across machines.
		private static string WalkKey(Harvested h)
		{
			return $"{h.Walk.NpcTemplateId}{h.Walk.TypeId}{h.Walk.ServerInitiated}{h.Walk.TriggerWorldObjectId}"
			       + $"{h.Walk.AnswerPath.Length:D3}{h.Walk.AnswerPath}{h.Walk.ConditionQuestId}{h.Walk.ConditionQuestState}{h.Walk.RequiresRepeatGreeting}";
		}
	}
}
