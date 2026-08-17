using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report quest-markers` (#848/#559): every `DispatchSetQuestMarker` retail ever sent us, resolved to the NPC it
	// was about and cross-tabbed against what the shipped quest data says that NPC is worth.
	//
	// ## The enum is the CLIENT's, and it has five values, not four
	//
	// The state byte reaches `n3VisualDynel::SetQuestMarker` (`FUN_005affb0`) unmodified - `GamecodeClient_t::
	// SetQuestMarker` (`FUN_01395460`) resolves the ID32 and passes the byte straight through - and that function is a
	// plain switch onto a particle effect:
	//
	//   0 -> (none, clears the effect)   1 -> quest_new_inactive        2 -> quest_new_active
	//                                    3 -> quest_progress_inactive   4 -> quest_progress_active
	//
	// So the axes are the client's own: `new`(!) vs `progress`(?), each `active` (bright) or `inactive` (the greyed
	// form the community documents as the grey `!`). ⭐ That NAMES `0x01` from the client rather than from player
	// prose, and it turns up a fifth state - **`0x03` quest_progress_inactive** - which this report measures as
	// **never sent**, in any capture, in any of the marker packets below. Retail's server collapses whatever `3` was
	// meant for onto another value.
	//
	// ⛔ And the client applies NO level logic of its own: it plays the effect it is told to play. So the wiki's
	// "grey means come back at a higher level" and "a quest far below you shows nothing" are both SERVER decisions
	// about which byte to send, not client-side rendering rules - neither can be recovered by more RE of the client.
	//
	// ## What this report tests
	//
	// `QuestMarkerResolver` derives "does this NPC have anything for you" from the dialogue graph's GRANTS alone. The
	// quest data holds a second relation the resolver never reads: a goal's `TargetTemplateId`, i.e. the NPCs a quest
	// SENDS you to. Turach grants two quests and is the target of four; Laranga grants two and is the target of six.
	// So the report puts both beside each NPC's observed states, and the population that decides between them is the
	// NPCs that only ever showed `0x00` - retail saying "this one is scenery, for this player, right now".
	//
	// Exit code is 0 always - a measurement, not a gate.
	public static partial class ReportCli
	{
		private static readonly uint MarkerWireOpcode = CapturedPacket.Wire(GameCharInterfaceMethods.DispatchSetQuestMarker);

		private static void QuestMarkers(IReadOnlyList<NamedCapture> captures)
		{
			var observations = new List<MarkerObservation>();
			var unresolved = 0;

			foreach (var c in captures)
			{
				// ⚠️ A handle is only unique inside one session AND only until it is recycled (#731), so the NPC a
				// marker is about is the most recent spawn of that handle BEFORE the marker, never the first or last.
				var spawns = NpcSpawnExtractor.Extract(c.Packets);
				var byHandle = spawns.GroupBy(s => s.Handle).ToDictionary(g => g.Key, g => g.OrderBy(s => s.SourceIndex).ToList());

				// The player's journal, replayed. `QuestIIR` states every transition (2 = active, 4 = completed) and
				// `QuestFullUpdateIIR` carries the whole entry on grant AND on every re-display - which is what makes
				// a capture that opens mid-life readable at all, since the login burst re-displays the journal the
				// player already had. Both are folded into one "state as of packet i" walk.
				var journal = QuestJournalWalk(c);

				for (var i = 0; i < c.Packets.Count; i++)
				{
					var p = c.Packets[i];
					if (!p.IsDownload || p.WireOpcode != MarkerWireOpcode)
						continue;

					var body = p.Body;
					if (body.Length < 9)
						continue;

					var handle = body.ReadU32Be(4);
					var state = body[8];
					var spawn = byHandle.TryGetValue(handle, out var list)
						? list.LastOrDefault(s => s.SourceIndex <= i) ?? list[0]
						: null;

					if (spawn == null)
					{
						unresolved++;
						continue;
					}

					var held = new HashSet<uint>();
					var completed = new HashSet<uint>();
					foreach (var (at, questId, questState) in journal)
					{
						if (at > i)
							break;

						(questState == QuestCodec.StateCompleted ? completed : held).Add(questId);
						(questState == QuestCodec.StateCompleted ? held : completed).Remove(questId);
					}

					observations.Add(new MarkerObservation(c.Name, spawn.TemplateId, spawn.Name, state, held, completed));
				}
			}

			Console.WriteLine($"=== quest markers over {captures.Count} capture(s) ===");
			Console.WriteLine();

			var states = observations.GroupBy(o => o.State).OrderBy(g => g.Key).ToList();
			Console.WriteLine($"{observations.Count:N0} marker packet(s) resolved to an NPC; {unresolved:N0} on a handle the dump never spawned.");
			Console.WriteLine();
			Console.WriteLine("  state  client particle effect        packets   NPCs");
			foreach (var name in MarkerEffects)
			{
				var g = states.FirstOrDefault(s => s.Key == name.Key);
				Console.WriteLine($"   0x{name.Key:X2}   {name.Value,-28}  {g?.Count() ?? 0,7:N0}  {g?.Select(o => o.Template).Distinct().Count() ?? 0,5}"
				                  + (g == null ? "   ⭐ NEVER SENT in the whole corpus" : ""));
			}

			MarkerCrossTab(observations);
		}

		// One capture's quest-state transitions in packet order: (packet index, quest id, state).
		private static List<(int At, uint QuestId, uint State)> QuestJournalWalk(NamedCapture c)
		{
			var walk = new List<(int At, uint QuestId, uint State)>();
			for (var i = 0; i < c.Packets.Count; i++)
			{
				var p = c.Packets[i];
				if (p is not { IsIirCarrier: true, IsDownload: true })
					continue;

				if (p.SubOpcode == QuestCodec.Opcode && p.TryParse(QuestCodec.Parse, out var q))
					walk.Add((i, q.QuestId, q.State));
				else if (p.SubOpcode == QuestFullUpdateCodec.Opcode && p.TryParse(QuestFullUpdateCodec.Parse, out var full) && full.Record != null)
					walk.Add((i, full.Record.QuestId, QuestCodec.StateActive));
			}

			return walk;
		}

		// The client's own names for the five values (`n3VisualDynel::SetQuestMarker`), in value order.
		private static readonly Dictionary<byte, string> MarkerEffects = new()
		{
			[0x00] = "(none - clears the effect)",
			[0x01] = "quest_new_inactive",
			[0x02] = "quest_new_active",
			[0x03] = "quest_progress_inactive",
			[0x04] = "quest_progress_active",
		};

		// ⭐ The question #848 is open on: what does retail know about an NPC that our GRANT-derived model does not?
		// Each observed NPC is put beside both relations the shipped quest data holds for it - what it GRANTS (the
		// dialogue graph, which is all `QuestMarkerResolver` reads) and what SENDS you to it (a goal's target).
		private static void MarkerCrossTab(List<MarkerObservation> observations)
		{
			using var db = OpenDb();

			var grants = db.ConversationEdges.Where(e => e.GrantQuestId > 0)
				.Select(e => new { e.From.NpcTemplateId, QuestId = e.GrantQuestId!.Value })
				.Distinct()
				.ToList()
				.GroupBy(g => g.NpcTemplateId)
				.ToDictionary(g => g.Key, g => g.Select(x => x.QuestId).Distinct().OrderBy(q => q).ToArray());

			var targeted = db.QuestGoalDefinitions.Where(g => g.TargetTemplateId != null && g.TargetTemplateId != 0)
				.Select(g => new { Template = g.TargetTemplateId!.Value, g.QuestDefinitionId })
				.Distinct()
				.ToList()
				.GroupBy(g => g.Template)
				.ToDictionary(g => g.Key, g => g.Select(x => x.QuestDefinitionId).Distinct().OrderBy(q => q).ToArray());

			// ⭐ The LINE each quest belongs to (#848). The relation that finally separates Casilda from Turach is not
			// about a quest at all - it is about whether the player has touched the CHAIN that quest sits in.
			var lineOf = db.QuestDefinitions.Where(q => q.QuestLineId > 0)
				.Select(q => new { q.QuestId, q.QuestLineId })
				.ToDictionary(q => q.QuestId, q => q.QuestLineId);

			var perNpc = observations.GroupBy(o => o.Template)
				.Select(g => new
				{
					Template = g.Key,
					Name = g.First().Name,
					Total = g.Count(),
					Zero = g.Count(o => o.State == 0),
					NonZero = g.Count(o => o.State != 0),
					States = g.GroupBy(o => o.State).OrderBy(s => s.Key).ToDictionary(s => s.Key, s => s.Count()),
					Grants = grants.GetValueOrDefault(g.Key, []),
					Targeted = targeted.GetValueOrDefault(g.Key, []),
				})
				.ToList();

			Console.WriteLine();
			Console.WriteLine($"=== {perNpc.Count} NPC template(s) ever carried a marker ===");
			Console.WriteLine();

			// ⭐ THE TEST. If the marker only ever described what an NPC GRANTS, then no NPC that grants nothing could
			// carry a non-zero marker. Every one that does is retail knowing something the resolver does not - and the
			// second column says whether the quest data already holds it.
			var lit = perNpc.Where(n => n.NonZero > 0).ToList();
			var litNoGrant = lit.Where(n => n.Grants.Length == 0).ToList();
			var litNoRelation = litNoGrant.Where(n => n.Targeted.Length == 0).ToList();

			Console.WriteLine($"  {lit.Count} of them were ever lit (a state other than 0x00):");
			Console.WriteLine($"    {lit.Count - litNoGrant.Count,4} GRANT a quest in the dialogue graph - the relation QuestMarkerResolver reads");
			Console.WriteLine($"    {litNoGrant.Count - litNoRelation.Count,4} grant NOTHING but are the TARGET of a quest goal - a relation it does not read");
			Console.WriteLine($"    {litNoRelation.Count,4} have neither - retail lights them off something the shipped data does not hold at all");
			Console.WriteLine();

			// ⭐⭐ THE FINDING. Split each STATE's own NPC population by which relation the shipped data holds for it.
			// The two states are not drawn from the same population at all, and that is what names them.
			Console.WriteLine("  ⭐ each state's NPCs, by which quest relation they have:");
			Console.WriteLine();
			Console.WriteLine("  state                     NPCs   grants a quest   only a goal TARGET   neither");
			foreach (var (value, effect) in MarkerEffects.Where(e => e.Key != 0))
			{
				var npcs = perNpc.Where(n => n.States.ContainsKey(value)).ToList();
				if (npcs.Count == 0)
				{
					Console.WriteLine($"   0x{value:X2} {effect,-22}     0   (never sent)");
					continue;
				}

				var granters = npcs.Count(n => n.Grants.Length > 0);
				var targets = npcs.Count(n => n.Grants.Length == 0 && n.Targeted.Length > 0);
				Console.WriteLine($"   0x{value:X2} {effect,-22}  {npcs.Count,4}   {granters,7} {granters * 100.0 / npcs.Count,5:0}%   {targets,10} {targets * 100.0 / npcs.Count,5:0}%   {npcs.Count - granters - targets,5}");
			}

			Console.WriteLine();
			Console.WriteLine("  the lit NPCs, by how often (grants = dialogue-graph, targeted-by = a goal sends you here):");
			Console.WriteLine();
			Console.WriteLine("  npc                        template   lit    0x00   states seen                 grants               targeted-by");
			foreach (var n in lit.OrderByDescending(n => n.NonZero).Take(40))
				Console.WriteLine($"  {Trim(n.Name, 25),-25}  {n.Template,-9}  {n.NonZero,5}  {n.Zero,5}   {Trim(string.Join(" ", n.States.Select(s => $"0x{s.Key:X2}x{s.Value}")), 25),-25}  {Trim(string.Join(",", n.Grants), 18),-18}  {Trim(string.Join(",", n.Targeted), 30)}");

			if (litNoRelation.Count > 0)
			{
				Console.WriteLine();
				Console.WriteLine("  ⚠️ lit with NO quest relation in the shipped data at all - each one is a harvest gap:");
				foreach (var n in litNoRelation.OrderByDescending(n => n.NonZero).Take(20))
					Console.WriteLine($"    {Trim(n.Name, 25),-25}  {n.Template,-9}  lit {n.NonZero,4}  {string.Join(" ", n.States.Select(s => $"0x{s.Key:X2}x{s.Value}"))}");
			}

			// The other half of the same test, and the one that decides whether "lit" can be predicted at all: an NPC
			// the quest data DOES relate to the player, which retail nonetheless never lights.
			var dark = perNpc.Where(n => n.NonZero == 0 && (n.Grants.Length > 0 || n.Targeted.Length > 0)).ToList();
			Console.WriteLine();
			Console.WriteLine($"  {dark.Count} NPC(s) are related to a quest and were NEVER lit - 0x00 every time:");
			foreach (var n in dark.OrderByDescending(n => n.Zero).Take(15))
				Console.WriteLine($"    {Trim(n.Name, 25),-25}  {n.Template,-9}  0x00 x{n.Zero,-5}  grants {Trim(string.Join(",", n.Grants), 12),-12}  targeted-by {Trim(string.Join(",", n.Targeted), 30)}");

			RulePredictorTest(observations, grants, targeted, lineOf);

			Console.WriteLine();
			Console.WriteLine("  ⚠️ Both lists are POPULATIONS, not verdicts. A never-lit giver may simply have been observed only by");
			Console.WriteLine("     players who had already finished it, and a lit NPC with no relation may be a quest we never harvested.");
			Console.WriteLine("     What they bound is how much of retail's answer the shipped data could reproduce at all.");
		}

		// ⭐⭐ THE PREDICTOR TEST (#848). Every observation is scored against the four relations the shipped data can
		// express, using the player's journal AS OF THAT PACKET rather than at the end of the capture. The point is
		// not to declare a rule but to see which relation the observed states actually track - a relation that fires
		// on 0x00 as often as on 0x02 is not what retail is describing, whatever it sounds like.
		//
		// ⚠️ Each row is a PACKET, and retail re-sends the same answer for every NPC on every poll, so a single
		// long-parked session weights heavily. The per-NPC table above is the counterweight; read them together.
		private static void RulePredictorTest(List<MarkerObservation> observations,
			Dictionary<uint, uint[]> grants, Dictionary<uint, uint[]> targeted, Dictionary<uint, uint> lineOf)
		{
			// Each observation's LINE view: which chains this NPC appears in at all, and which chains the player has
			// already touched (any quest of that line held or completed as of this packet).
			foreach (var o in observations)
			{
				foreach (var q in grants.GetValueOrDefault(o.Template, []).Concat(targeted.GetValueOrDefault(o.Template, [])))
					if (lineOf.TryGetValue(q, out var line))
						o.NpcLines.Add(line);

				foreach (var q in o.Held.Concat(o.Completed))
					if (lineOf.TryGetValue(q, out var line))
						o.TouchedLines.Add(line);
			}

			Console.WriteLine();
			Console.WriteLine("=== ⭐ which quest RELATION do the states track? (#848) ===");
			Console.WriteLine();
			Console.WriteLine("  Each marker packet, scored against the player's journal as of that packet:");
			Console.WriteLine();
			Console.WriteLine("  relation                                      0x00     0x01     0x02     0x04   lit%");

			foreach (var (name, holds) in Relations)
			{
				var firing = observations.Where(o => holds(o, grants.GetValueOrDefault(o.Template, []), targeted.GetValueOrDefault(o.Template, []))).ToList();
				if (firing.Count == 0)
				{
					Console.WriteLine($"  {name,-42}  (never fires)");
					continue;
				}

				var byState = firing.GroupBy(o => o.State).ToDictionary(g => g.Key, g => g.Count());
				var lit = firing.Count(o => o.State != 0);
				Console.WriteLine($"  {name,-42} {byState.GetValueOrDefault((byte)0),7:N0}  {byState.GetValueOrDefault((byte)1),7:N0}  {byState.GetValueOrDefault((byte)2),7:N0}  {byState.GetValueOrDefault((byte)4),7:N0}  {lit * 100.0 / firing.Count,5:0}%");
			}

			// ⭐ The same numbers read the other way, which is the direction the question is actually asked in: given
			// that retail sent SILVER, what was true of that NPC? A relation holding on 85% of the silvers says what
			// silver is ABOUT, even where the relation's own lit% is low because it also holds on quiet NPCs.
			Console.WriteLine();
			Console.WriteLine("  ⭐ read the other way - of the packets carrying each state, how many had each relation:");
			Console.WriteLine();
			Console.WriteLine("  relation                                    0x01 silver !   0x02 gold !    0x04 gold ?");
			foreach (var (name, holds) in Relations)
			{
				var cells = new[] { (byte)1, (byte)2, (byte)4 }.Select(st =>
				{
					var of = observations.Where(o => o.State == st).ToList();
					var hit = of.Count(o => holds(o, grants.GetValueOrDefault(o.Template, []), targeted.GetValueOrDefault(o.Template, [])));
					return of.Count == 0 ? "        -" : $"{hit,6:N0} {hit * 100.0 / of.Count,3:0}%";
				});
				Console.WriteLine($"  {name,-42} {string.Join("  ", cells)}");
			}

			// ⭐⭐ The one relation that comes out ABSOLUTE, printed as a gate rather than a percentage - and printed
			// with its exceptions named, because a claim of "zero" is only worth having if the next capture that
			// breaks it says so out loud.
			var allStarted = observations.Where(o => o.NpcLines.Count > 0 && o.NpcLines.All(o.TouchedLines.Contains)).ToList();
			var silverExceptions = allStarted.Where(o => o.State == 0x01).ToList();
			Console.WriteLine();
			Console.WriteLine($"  ⭐ SILVER REQUIRES AN UNTOUCHED LINE. Of the {allStarted.Count} packet(s) about an NPC whose every quest line the");
			Console.WriteLine($"     player had ALREADY started, {silverExceptions.Count} carried silver.");
			foreach (var g in allStarted.GroupBy(o => o.State).OrderBy(g => g.Key))
				Console.WriteLine($"       0x{g.Key:X2} x{g.Count(),-5}  {string.Join(", ", g.Select(o => o.Name).Distinct().Take(6))}");
			foreach (var o in silverExceptions.Take(10))
				Console.WriteLine($"       ⛔ EXCEPTION: {o.Name} in {Trim(o.Capture, 50)}");

			var goldExceptions = allStarted.Where(o => o.State == 0x02).ToList();
			foreach (var o in goldExceptions.Take(5))
				Console.WriteLine($"       ⚠️ gold '!' here too: {o.Name} in {Trim(o.Capture, 50)} - held {o.Held.Count}, completed {o.Completed.Count}");

			Console.WriteLine();
			Console.WriteLine("     ⚠️ The journal is what the CAPTURE showed, so `touched` is a LOWER bound - a quest finished before");
			Console.WriteLine("        the dump opened and never re-displayed is invisible. That biases toward calling a line UNstarted,");
			Console.WriteLine("        which shrinks the population above rather than the exception count inside it: the zero is on the");
			Console.WriteLine("        packets where the player is KNOWN to have started every line the NPC is in, and is safe that way.");

			Console.WriteLine();
			Console.WriteLine("  The baseline is the whole population: an NPC picked at random is lit "
			                  + $"{observations.Count(o => o.State != 0) * 100.0 / observations.Count:0.0}% of the time.");
			Console.WriteLine("  A relation only tells us something insofar as its lit% beats that.");
		}

		// The relations the shipped quest data can express about an NPC, given a player's journal at one moment.
		// `grants` is the dialogue graph's own grants (what QuestMarkerResolver reads); `targeted` is the quests whose
		// GOALS send the player to this NPC - the relation it does not read.
		private static readonly (string Name, Func<MarkerObservation, uint[], uint[], bool> Holds)[] Relations =
		[
			("grants a quest (any state)", (_, g, _) => g.Length > 0),
			("grants one NOT held and NOT completed", (o, g, _) => g.Any(q => !o.Held.Contains(q) && !o.Completed.Contains(q))),
			("grants one the player HOLDS", (o, g, _) => g.Any(o.Held.Contains)),
			("grants one the player COMPLETED", (o, g, _) => g.Any(o.Completed.Contains)),
			("a goal TARGETS it (any state)", (_, _, t) => t.Length > 0),
			("a goal of a HELD quest targets it", (o, _, t) => t.Any(o.Held.Contains)),
			("a goal of an untouched quest targets it", (o, _, t) => t.Any(q => !o.Held.Contains(q) && !o.Completed.Contains(q))),
			("a goal of a COMPLETED quest targets it", (o, _, t) => t.Any(o.Completed.Contains)),
			("neither relation at all", (_, g, t) => g.Length == 0 && t.Length == 0),

			// ⭐⭐ THE LINE RELATIONS (#848). "A quest line runs through this NPC that the player has not begun" is the
			// reading that survives both awkward cases at once: Turach stays silver because lines 222 and 1305 pass
			// through him and the player never started either, while Casilda goes DARK the moment her quest is taken
			// because her only line (1054) is now one the player is ON - and a line in progress lights her only when
			// she is actually actionable.
			("in a line the player has NOT started", (o, _, _) => o.NpcLines.Any(l => !o.TouchedLines.Contains(l))),
			("in a line the player HAS started", (o, _, _) => o.NpcLines.Any(o.TouchedLines.Contains)),
			("ONLY in lines the player has started", (o, _, _) => o.NpcLines.Count > 0 && o.NpcLines.All(o.TouchedLines.Contains)),
		];

		private sealed record MarkerObservation(string Capture, uint Template, string Name, byte State, HashSet<uint> Held, HashSet<uint> Completed)
		{
			// The quest LINES this NPC appears in (as granter or goal target), and the lines the player had already
			// touched when this packet was sent. Filled by RulePredictorTest, which is where the line map is loaded.
			public HashSet<uint> NpcLines { get; } = [];
			public HashSet<uint> TouchedLines { get; } = [];
		}
	}
}
