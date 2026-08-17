using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.GameServer.Quest;
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
			var multiViewerCaptures = 0;

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

				// The VIEWER's level, off their own spawn body (`player_stats` is what separates a player spawn from
				// an NPC one). Needed for #848's other thread: the wiki claims a quest far BELOW the player draws no
				// marker, and the corpus evidence for that has never been separated from the line rule.
				// ⚠️ TWO clients can share one dump and NOTHING in a marker packet names its recipient, so a capture
				// with more than one player in it cannot attribute a level to the viewer at all. Those are dropped
				// from the level test rather than guessed at - the ticket's own history has one wrong attribution in
				// it already (keyed on a capture FILENAME), and this is the same failure wearing a different hat.
				var playerLevels = new HashSet<uint>();
				foreach (var p in c.Packets)
				{
					if (p is not { IsIirCarrier: true, IsDownload: true, SubOpcode: SimpleCharFullUpdateCodec.Opcode })
						continue;
					if (p.TryParse(SimpleCharFullUpdateCodec.Parse, out var self) && self.Character?.player_stats != null && self.Character.Stats?.Level is > 0 and var lvl)
						playerLevels.Add(lvl);
				}

				var playerLevel = playerLevels.Count == 1 ? playerLevels.Single() : 0;
				if (playerLevels.Count > 1)
					multiViewerCaptures++;

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

					observations.Add(new MarkerObservation(c.Name, spawn.TemplateId, spawn.Name, state, held, completed) { PlayerLevel = playerLevel });
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

			if (multiViewerCaptures > 0)
				Console.WriteLine($"({multiViewerCaptures} capture(s) hold more than one player and are excluded from the level test below - nothing names a marker's recipient.)");

			MarkerCrossTab(observations);
			ScoreShippingRule(observations);
		}

		// ⭐ What would WE send? The cross-tab above scores RELATIONS against retail; this scores the rule the server
		// actually ships (#857), so a change to it is measurable rather than argued.
		//
		// It calls `QuestMarkerResolver.LightsSilver` directly rather than restating the rule. A rule measured in one
		// implementation and shipped in another is a rule with no measurement - that is why the resolver exposes it,
		// the same reason `LineReached` is public.
		//
		// ⚠️ Scoped to the packets the rule actually DECIDES: retail sent 0x00 or 0x01, and the viewer's level is
		// known. The gold states come from the giver fold and the talk-goal check, neither of which this rule touches,
		// and a marker whose viewer level we never captured cannot be scored at all (the level band needs it).
		private static void ScoreShippingRule(List<MarkerObservation> observations)
		{
			using var db = OpenDb();
			var defs = db.QuestDefinitions.ToDictionary(q => q.QuestId, q => q);

			var grants = db.ConversationEdges.Where(e => e.GrantQuestId > 0)
				.Select(e => new { e.From.NpcTemplateId, QuestId = e.GrantQuestId!.Value })
				.Distinct()
				.ToList()
				.GroupBy(g => g.NpcTemplateId)
				.ToDictionary(g => g.Key, g => g.Select(x => x.QuestId).Distinct().ToArray());

			// The same exclusion the shipping map applies (`GetQuestGoalTargetMap`): a KILL or LOOT target is not a
			// marker relation. Restated here rather than shared because this report also scores the UNrestricted
			// relation in its cross-tab above, and the two questions want different sets.
			var targeted = db.QuestGoalDefinitions.Where(g => g.TargetTemplateId != null && g.TargetTemplateId != 0
			                                                  && g.GoalType != QuestGoalType.Kill && g.GoalType != QuestGoalType.LootFromNpc)
				.Select(g => new { Template = g.TargetTemplateId!.Value, g.QuestDefinitionId })
				.Distinct()
				.ToList()
				.GroupBy(g => g.Template)
				.ToDictionary(g => g.Key, g => g.Select(x => x.QuestDefinitionId).Distinct().ToArray());

			int hit = 0, miss = 0, over = 0, correctDark = 0;
			// The LINE clause alone, scored over every 0x00/0x01 packet rather than only the ones whose viewer level
			// we captured. The two clauses have to be scored apart: the level band needs a viewer level, and only 50
			// of the 456 silver packets have one, so folding them together scores the line rule on 11% of its evidence
			// and reads as the rule failing when it is the sample that is missing.
			int lineHit = 0, lineMiss = 0, lineOver = 0;
			var missReason = new Dictionary<string, int>();
			var overNpc = new Dictionary<string, int>();
			foreach (var o in observations)
			{
				if (o.State != 0x00 && o.State != 0x01)
					continue;

				// ⚠️ PRECEDENCE. The silver rule is the LAST thing `MarkerStateFor` consults - a giver with a quest
				// still on offer is answered gold by the fold above it, and a talk goal is answered 0x04 before that.
				// Scoring the silver rule over those packets measures a branch the server never reaches, and it is not
				// a small effect: it puts Casilda at the top of the over-lit list, when retail's 0x00 there is her
				// giver state and the resolver would have returned gold. Skip anything the fold owns.
				var outstandingGrant = grants.GetValueOrDefault(o.Template, []).Any(q => !o.Held.Contains(q) && !o.Completed.Contains(q));
				if (outstandingGrant)
					continue;

				var through = grants.GetValueOrDefault(o.Template, []).Concat(targeted.GetValueOrDefault(o.Template, []))
					.Distinct()
					.Select(q => defs.GetValueOrDefault(q))
					.ToList();
				var startedLines = o.Held.Concat(o.Completed)
					.Select(q => defs.GetValueOrDefault(q)?.QuestLineId ?? 0)
					.Where(l => l != 0)
					.ToHashSet();

				// Level 0 makes the band vacuously satisfiable for every quest that states a level, which is exactly
				// "the line clause on its own" - the rule with its second gate removed rather than a separate model.
				var lineOnly = QuestMarkerResolver.LightsSilver(through, startedLines, 0, applyLevelBand: false);
				if (o.State == 0x01)
				{
					if (lineOnly)
						lineHit++;
					else
						lineMiss++;
				}
				else if (lineOnly)
				{
					lineOver++;
				}

				if (o.PlayerLevel == 0)
					continue;

				var predicted = QuestMarkerResolver.LightsSilver(through, startedLines, o.PlayerLevel);
				if (o.State == 0x01)
				{
					if (predicted)
					{
						hit++;
					}
					else
					{
						miss++;
						// ⭐ WHY it was missed, in the order the rule tests. A bare recovery percentage says the rule
						// is wrong; this says which of its clauses is doing the rejecting - and whether the answer is
						// a rule change at all or a harvest gap wearing one.
						var unstarted = through.Where(d => d != null && !startedLines.Contains(d.QuestLineId)).ToList();
						missReason[through.Count == 0
							? "no quest relation in the shipped data at all"
							: unstarted.Count == 0
								? "every line through this NPC was already started"
								: unstarted.All(d => d!.Level == 0)
									? "the untouched quests state no level (Level 0)"
									: "level delta outside the observed band"] = missReason.GetValueOrDefault(
							through.Count == 0
								? "no quest relation in the shipped data at all"
								: unstarted.Count == 0
									? "every line through this NPC was already started"
									: unstarted.All(d => d!.Level == 0)
										? "the untouched quests state no level (Level 0)"
										: "level delta outside the observed band") + 1;
					}
				}
				else if (predicted)
				{
					over++;
					overNpc[o.Name] = overNpc.GetValueOrDefault(o.Name) + 1;
				}
				else
				{
					correctDark++;
				}
			}

			var silver = hit + miss;
			var dark = over + correctDark;
			Console.WriteLine();
			Console.WriteLine("=== ⭐ what the SHIPPING rule would send (QuestMarkerResolver.LightsSilver, #857) ===");
			Console.WriteLine();
            Console.WriteLine($"  Scored over the {silver + dark:N0} packet(s) where retail sent 0x00 or 0x01 and the viewer's level is known.");
			Console.WriteLine();
			Console.WriteLine($"  retail SILVER (0x01), {silver,6:N0}:   {hit,6:N0} we light   {miss,6:N0} we leave dark   -> {(silver == 0 ? 0 : hit * 100.0 / silver),5:0}% recovered");
			Console.WriteLine($"  retail DARK   (0x00), {dark,6:N0}:   {over,6:N0} we light   {correctDark,6:N0} we leave dark   -> {(dark == 0 ? 0 : over * 100.0 / dark),5:0}% over-lit");

			var lineSilver = lineHit + lineMiss;
			Console.WriteLine();
			Console.WriteLine("  ...and the LINE clause ALONE, over every 0x00/0x01 packet (no viewer level needed, so 9x the evidence):");
			Console.WriteLine($"  retail SILVER (0x01), {lineSilver,6:N0}:   {lineHit,6:N0} we light   {lineMiss,6:N0} we leave dark   -> {(lineSilver == 0 ? 0 : lineHit * 100.0 / lineSilver),5:0}% recovered");
			Console.WriteLine($"  retail DARK   (0x00):                 {lineOver,6:N0} we light");
			if (overNpc.Count > 0)
			{
				Console.WriteLine();
				Console.WriteLine("  the NPCs we would light where retail sent 0x00 - and whether retail EVER lights them:");
				foreach (var (name, n) in overNpc.OrderByDescending(r => r.Value).Take(15))
				{
					var everLit = observations.Where(x => x.Name == name).ToList();
					var states = everLit.GroupBy(x => x.State).OrderBy(g => g.Key).Select(g => $"0x{g.Key:X2}x{g.Count()}");
					Console.WriteLine($"    {n,5}  {name,-28}  retail over the whole corpus: {string.Join(" ", states)}");
				}
			}

			if (missReason.Count > 0)
			{
				Console.WriteLine();
				Console.WriteLine("  why each missed silver was left dark:");
				foreach (var (reason, n) in missReason.OrderByDescending(r => r.Value))
					Console.WriteLine($"    {n,6:N0}  {reason}");
			}

			Console.WriteLine();
			Console.WriteLine("  ⚠️ Reported, never asserted. Both numbers move with the corpus AND with the quest harvest -");
			Console.WriteLine("     an NPC retail lights off a grant we never captured is a harvest gap scored as a miss.");
		}

		// One capture's quest-state transitions in packet order: (packet index, quest id, state).
		//
		// ⚠️⚠️ The live IIRs are NOT the whole journal, and believing they were made this report lie about exactly the
		// population #848 cares about. `QuestIIR`/`QuestFullUpdateIIR` carry what happens DURING the session, and the
		// level-80 captures hold **one** such packet each - so every quest that character ever finished read as
		// "untouched", and a level-80 standing dark beside a Tortage NPC looked like evidence for a level rule when
		// it is just a quest they completed years earlier.
		//
		// The archive arrives instead inside the login SNAPSHOT, as the area-grouped completed-quest region (#390),
		// which is why it has to be read here too: the necromancer's snapshot alone carries ~1,540 of them.
		private static List<(int At, uint QuestId, uint State)> QuestJournalWalk(NamedCapture c)
		{
			var walk = new List<(int At, uint QuestId, uint State)>();
			for (var i = 0; i < c.Packets.Count; i++)
			{
				var p = c.Packets[i];
				if (!p.IsDownload)
					continue;

				if (SnapshotJournalReader.TryReadCompleted(p.Bytes, out var archive) && archive != null)
				{
					foreach (var q in archive.Areas.SelectMany(a => a.Quests).Where(q => q.QuestId != 0))
						walk.Add((i, q.QuestId, QuestCodec.StateCompleted));

					continue;
				}

				if (!p.IsIirCarrier)
					continue;

				if (p.SubOpcode == QuestCodec.Opcode && p.TryParse(QuestCodec.Parse, out var q2))
					walk.Add((i, q2.QuestId, q2.State));
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

			var levelOf = db.QuestDefinitions.Where(q => q.Level > 0)
				.Select(q => new { q.QuestId, q.Level })
				.ToDictionary(q => q.QuestId, q => q.Level);

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
			LevelVersusLineTest(observations, grants, targeted, levelOf);

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

		// ⭐⭐ #848 thread 2, re-examined. The claim on the ticket is that a quest far BELOW the player draws no marker
		// ("significantly lower-level characters will not have the exclamation mark"), bracketed from the corpus at
		// `1 < T <= 56` because a level-80 was seen dark beside 15 never-taken quests at delta 56-75.
		//
		// ⚠️ That evidence was never separated from the LINE rule, and the two predict the same darkness. This splits
		// them: every observation on an NPC with an UNTOUCHED quest is bucketed by the level gap to that quest AND by
		// whether the player had already started the NPC's lines. If the high-delta darkness lives entirely in the
		// `all lines started` column, the level rule is unsupported and the bracket is an artifact of the confound.
		private static void LevelVersusLineTest(List<MarkerObservation> observations,
			Dictionary<uint, uint[]> grants, Dictionary<uint, uint[]> targeted, Dictionary<uint, uint> levelOf)
		{
			Console.WriteLine();
			Console.WriteLine("=== ⭐ level gap vs the line rule - which explains a DARK marker? (#848 thread 2) ===");
			Console.WriteLine();

			var rows = new List<(int Delta, bool LineUntouched, byte State, string Capture)>();
			foreach (var o in observations.Where(o => o.PlayerLevel > 0))
			{
				// The untouched quest this NPC is related to whose level is CLOSEST to the player - the one that would
				// decide a "too low for you" rule, since any nearer quest is the one retail would advertise.
				var candidates = grants.GetValueOrDefault(o.Template, []).Concat(targeted.GetValueOrDefault(o.Template, []))
					.Where(q => !o.Held.Contains(q) && !o.Completed.Contains(q) && levelOf.ContainsKey(q))
					.Select(q => (int)o.PlayerLevel - (int)levelOf[q])
					.ToList();
				if (candidates.Count == 0)
					continue;

				rows.Add((candidates.MinBy(Math.Abs), o.NpcLines.Any(l => !o.TouchedLines.Contains(l)), o.State, o.Capture));
			}

			if (rows.Count == 0)
			{
				Console.WriteLine("  (no observation has both a viewer level and an untouched quest of known level)");
				return;
			}

			Console.WriteLine($"  {rows.Count:N0} observation(s) where the viewer's level is known AND the NPC has an untouched quest of known level.");
			Console.WriteLine("  Delta = viewer level - quest level; positive means the quest is BELOW the player.");
			Console.WriteLine();
			Console.WriteLine("  delta        line UNstarted: lit / total        all lines started: lit / total");
			foreach (var bucket in LevelBuckets)
			{
				var inBucket = rows.Where(r => bucket.Holds(r.Delta)).ToList();
				if (inBucket.Count == 0)
					continue;

				var untouched = inBucket.Where(r => r.LineUntouched).ToList();
				var started = inBucket.Where(r => !r.LineUntouched).ToList();
				Console.WriteLine($"  {bucket.Name,-12} {Cell(untouched),-36} {Cell(started)}");
			}

			// ⭐ The boundary, at one-level resolution. A bracket is only worth stating if the corpus actually has
			// observations either side of it, so this prints every delta that occurs rather than a chosen bucketing -
			// and prints how many distinct CAPTURES back each one, because a flip that happens inside one session is
			// a control and a flip between sessions is a confound waiting to be named.
			Console.WriteLine();
			Console.WriteLine("  per-level detail, line-UNstarted only (the column where a level rule could still live):");
			Console.WriteLine();
			Console.WriteLine("  delta   lit / total   captures");
			foreach (var g in rows.Where(r => r.LineUntouched).GroupBy(r => r.Delta).OrderBy(g => g.Key))
				Console.WriteLine($"  {g.Key,+5}   {g.Count(r => r.State != 0),4} / {g.Count(),-6}  {g.Select(r => r.Capture).Distinct().Count()}");

			// ⭐ The brackets, computed rather than eyeballed - and deliberately expressed as brackets, because the
			// corpus is SPARSE in delta (a handful of characters at a handful of levels) and the gap either side of
			// the lit band is unobserved rather than dark. Picking a number inside an unobserved gap would be
			// inventing data; naming the gap is the honest result.
			var byDelta = rows.Where(r => r.LineUntouched).GroupBy(r => r.Delta)
				.Select(g => (Delta: g.Key, Lit: g.Count(r => r.State != 0), Total: g.Count()))
				.OrderBy(x => x.Delta)
				.ToList();
			var litDeltas = byDelta.Where(x => x.Lit > 0).Select(x => x.Delta).ToList();
			if (litDeltas.Count > 0)
			{
				var lo = litDeltas.Min();
				var hi = litDeltas.Max();
				var darkBelow = byDelta.Where(x => x.Lit == 0 && x.Delta < lo).Select(x => x.Delta).DefaultIfEmpty(int.MinValue).Max();
				var darkAbove = byDelta.Where(x => x.Lit == 0 && x.Delta > hi).Select(x => x.Delta).DefaultIfEmpty(int.MaxValue).Min();

				Console.WriteLine();
				Console.WriteLine($"  ⭐ MARKERS LIGHT ONLY IN A BAND around the player's level: every lit observation is at delta {lo}..{hi},");
				Console.WriteLine("     and every delta outside it is unanimously dark.");
				if (darkBelow != int.MinValue)
					Console.WriteLine($"       quest ABOVE the player: lit at {lo}, dark at {darkBelow} -> the cutoff is bracketed {darkBelow} < T <= {lo}, gap unobserved");
				if (darkAbove != int.MaxValue)
					Console.WriteLine($"       quest BELOW the player: lit at {hi}, dark at {darkAbove} -> the cutoff is bracketed {hi} < T <= {darkAbove}, gap unobserved");
			}

			Console.WriteLine();
			Console.WriteLine("  ⭐ Read the columns, not the rows: if the far-below-you darkness is confined to the RIGHT column,");
			Console.WriteLine("     the line rule already explains it and the level cutoff has no evidence of its own.");
			return;

			static string Cell(List<(int Delta, bool LineUntouched, byte State, string Capture)> rows)
			{
				var lit = rows.Count(r => r.State != 0);
				return rows.Count == 0 ? "-" : $"{lit,5} / {rows.Count,-6} = {lit * 100.0 / rows.Count,5:0}% lit";
			}
		}

		private static readonly (string Name, Func<int, bool> Holds)[] LevelBuckets =
		[
			("<= -10", d => d <= -10),
			("-9..-2", d => d is >= -9 and <= -2),
			("-1..+1", d => d is >= -1 and <= 1),
			("+2..+9", d => d is >= 2 and <= 9),
			("+10..+29", d => d is >= 10 and <= 29),
			("+30..+55", d => d is >= 30 and <= 55),
			(">= +56", d => d >= 56),
		];

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

			// The VIEWER's own level in this capture (0 = their spawn body was never captured).
			public uint PlayerLevel { get; init; }
		}
	}
}
