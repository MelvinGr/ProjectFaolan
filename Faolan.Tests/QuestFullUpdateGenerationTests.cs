using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// CONSTRUCT, don't replay - the live-grant analogue of CapturedObjectiveRegions_RebuildFromTheDatabase_ByteExact
	// (#866): rebuild every captured QuestFullUpdate's GOAL LIST from the quest's DB rows through the live builder and
	// compare byte-for-byte. Retail's full-update carries the quest's whole goal list (28 of the 79 captured bodies
	// hold 2+ goals, all of them grant-shaped), so the builder emitting one synthesized goal was a measured
	// divergence, not a simplification.
	//
	// The RECORD scalars around the list are deliberately not compared: Level/F0/XpReward vary per capture and per
	// variant, and the Casilda golden in QuestTests pins the record layout already. This gate is about the goals.
	public class QuestFullUpdateGenerationTests(ITestOutputHelper o)
	{
		[Fact]
		public async Task CapturedLiveGrants_RebuildTheirGoalLists_ByteExact()
		{
			await using var db = new DatabaseContext(TestDb.Options(TestPaths.FaolanDb));
			var defs = await db.QuestDefinitions.Include(q => q.Goals).Include(q => q.TextRefs).ToDictionaryAsync(q => q.QuestId, TestContext.Current.CancellationToken);

			int total = 0, fallback = 0, exact = 0, partialShort = 0;
			var violations = new List<string>();

			foreach (var (source, body) in CorpusExtract.OfType((uint)IirTypes.QuestFullUpdateIIR))
			{
				QuestFullUpdateIIR captured;
				var offset = body.Length > 4 && body[0] == 0 ? 4 : 0;
				try
				{
					captured = QuestFullUpdateCodec.Parse(new ReadOnlySpan<byte>(body, offset, body.Length - offset));
				}
				catch (CodecParseException)
				{
					continue; // the corpus round-trip gate owns parse coverage
				}

				total++;
				if (!defs.TryGetValue(captured.Record.QuestId, out var def))
				{
					violations.Add($"{source}: quest {captured.Record.QuestId} has no definition row");
					continue;
				}

				var capturedGoals = captured.Record.Objectives!.Goals;
				var built = QuestFullUpdates.ForQuest(capturedGoals[0].Instance, def, captured.Record.Level).Record.Objectives!.Goals;

				// The synthesized single-goal fallback (rows without harvested texts / kind words). Not asserted
				// against the capture - it never matched and its quests are exactly the harvest gap - but COUNTED,
				// so the number is on record and only ever shrinks as the harvest completes.
				if (!QuestFullUpdates.CanBuildFullGoalList(def))
				{
					fallback++;
					continue;
				}

				// The full-list path fired. Every built goal must byte-match its captured twin, and any goal the
				// capture carries beyond the built set must be a PARTIAL row - the secondary records recovered from
				// full-updates without their wire scalars, which the region builder excludes by design.
				var capturedById = capturedGoals.ToDictionary(g => g.GoalId);
				var ok = true;
				foreach (var goal in built)
				{
					if (!capturedById.TryGetValue(goal.GoalId, out var twin))
					{
						violations.Add($"{source}: quest {def.QuestId} built goal {goal.GoalId} the capture does not carry");
						ok = false;
						continue;
					}

					var builtBytes = QuestObjectiveGoalCodec.Build(goal);
					var wantBytes = QuestObjectiveGoalCodec.Build(twin);
					if (!builtBytes.SequenceEqual(wantBytes))
					{
						violations.Add($"{source}: quest {def.QuestId} goal {goal.GoalId} differs from the capture");
						ok = false;
					}
				}

				var builtIds = built.Select(g => g.GoalId).ToHashSet();
				var missing = capturedGoals.Where(g => !builtIds.Contains(g.GoalId)).ToList();
				foreach (var m in missing)
				{
					var row = def.Goals.FirstOrDefault(g => g.GoalId == m.GoalId);
					if (row is not { Partial: true })
					{
						violations.Add($"{source}: quest {def.QuestId} misses captured goal {m.GoalId} and its row is not Partial");
						ok = false;
					}
				}

				if (!ok)
					continue;
				if (missing.Count > 0)
					partialShort++;
				else
					exact++;
			}

			o.WriteLine($"captured live grants: {total} - full-list exact {exact}, short only by Partial rows {partialShort}, synthesized fallback {fallback}");
			Assert.Empty(violations);

			// Every captured grant rebuilds: the QFU harvest completes the goal rows (trailing text, wire N, echoes -
			// #866), so the synthesized fallback fires for NO captured quest. The corpus slice is frozen and only
			// ever grows, so this can move upward with it but a drop is a regression in the builder or the rows.
			Assert.True(exact >= 79, $"full-list exact fell to {exact} (was 79) - the live builder or the quest rows regressed");
			Assert.True(fallback == 0, $"{fallback} captured grants fell back to the synthesized single goal - their rows lost harvest coverage");
		}
	}
}
