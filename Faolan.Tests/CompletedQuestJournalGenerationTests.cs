using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// CONSTRUCT, don't replay: rebuild captured completed-quest journal regions from DB rows and compare byte-for-byte
	// against the capture (#390). This is the gate that matters for generation - the parse round-trip only proves the
	// codec agrees with itself, whereas this proves the harvested QuestDefinitions/QuestAreas actually reproduce the
	// wire.
	//
	// Ordering is an INPUT, not stored state: the wire order is per-character and not derivable, and the client
	// re-sorts for display anyway, so the test feeds each capture's own order and checks the CONTENT.
	public class CompletedQuestJournalGenerationTests(ITestOutputHelper o)
	{
		// The @609 OBJECTIVE region, rebuilt from DB rows and compared byte-for-byte with the capture. The last
		// generated region without such a gate - and every region that lacked one has shipped malformed at least once
		// today while the suite stayed green.
		[Fact]
		public async Task CapturedObjectiveRegions_RebuildFromTheDatabase_ByteExact()
		{
			await using var db = new DatabaseContext(TestDb.Options(TestPaths.FaolanDb));
			var defs = await db.QuestDefinitions.Include(q => q.Goals).ToDictionaryAsync(q => q.QuestId, TestContext.Current.CancellationToken);

			int compared = 0, exact = 0, skipped = 0, lenDiff = 0, sameLenDiff = 0;
			_ = lenDiff;
			var failByQuest = new Dictionary<uint, int>();
			var fieldDiffs = new Dictionary<string, int>();
			var seenQuest = new HashSet<uint>();
			var shown = 0;
			var okByQuest = new HashSet<uint>();
			var first = "";

			foreach (var (source, sub, _) in CompletedQuestJournalTests.SubClassesForProbe())
			foreach (var rec in sub.Quests.Records)
			{
				if (rec.Objectives is not { Goals.Count: > 0 } captured)
					continue;

				if (!defs.TryGetValue(rec.QuestId, out var def) || def.Goals.Count == 0)
				{
					skipped++;
					continue;
				}

				// Goals carry the live player's instance; take it from the capture so only CONTENT is compared.
				var instance = captured.Goals[0].Instance;
				var built = QuestObjectiveRegion.Build(def, instance).ToArray();
				var want = captured.ToArray();
				compared++;
				if (built.SequenceEqual(want))
				{
					exact++;
					okByQuest.Add(rec.QuestId);
					continue;
				}

				failByQuest[rec.QuestId] = failByQuest.GetValueOrDefault(rec.QuestId) + 1;

				// Aggregate WHICH field differs, so the tail can be worked in priority order.
				var rebuiltGoals = QuestObjectiveRegion.Parse(built, 0).Goals;
				if (rebuiltGoals.Count != captured.Goals.Count)
					Bump(fieldDiffs, "GOAL COUNT");
				else
				{
					for (var gi = 0; gi < captured.Goals.Count; gi++)
					{
						var c = captured.Goals[gi];
						var b = rebuiltGoals[gi];
						if (c.GoalId != b.GoalId)
							Bump(fieldDiffs, "GoalId/order");
						if (c.N != b.N)
							Bump(fieldDiffs, "N");
						if (c.K != b.K)
							Bump(fieldDiffs, "K/separator");
						if (c.SubId != b.SubId)
							Bump(fieldDiffs, "SubId");
						if (c.MapId != b.MapId)
							Bump(fieldDiffs, "MapId");
						if (c.MarkerX != b.MarkerX || c.MarkerY != b.MarkerY)
							Bump(fieldDiffs, "Marker");
						if (c.RequiredCount != b.RequiredCount)
							Bump(fieldDiffs, "RequiredCount");
						if (c.Unknown1 != b.Unknown1)
							Bump(fieldDiffs, "Unknown1");
						if (c.Unknown2 != b.Unknown2)
							Bump(fieldDiffs, "Zero2");
						if (c.Lead != b.Lead)
							Bump(fieldDiffs, "Lead");
						if (c.GoalIdRepeat != b.GoalIdRepeat)
							Bump(fieldDiffs, "GoalIdRepeat");
						if (!c.Reserved.SequenceEqual(b.Reserved))
							Bump(fieldDiffs, "Reserved");
						if (!c.Objective.SequenceEqual(b.Objective))
							Bump(fieldDiffs, "Objective text");
						if (!c.TextRecord.SequenceEqual(b.TextRecord))
							Bump(fieldDiffs, "Trailing text");
					}
				}

				if (built.Length != want.Length)
					lenDiff++;
				else
					sameLenDiff++;

				if (shown < 4 && seenQuest.Add(rec.QuestId))
				{
					shown++;
					first = "see log";
					o.WriteLine($"  MISMATCH {source[..Math.Min(30, source.Length)]} quest {rec.QuestId}: built {built.Length} B vs captured {want.Length} B; " +
					            $"goals db={def.Goals.Count} cap={captured.Goals.Count}; builtGoalIds=[{string.Join(",", QuestObjectiveRegion.Parse(built, 0).Goals.Select(x => x.GoalId))}] capGoalIds=[{string.Join(",", captured.Goals.Select(x => x.GoalId))}]");
					var rebuilt = QuestObjectiveRegion.Parse(built, 0);
					for (var gi = 0; gi < captured.Goals.Count; gi++)
					{
						var cg = captured.Goals[gi];
						var bg = rebuilt.Goals[gi];
						o.WriteLine($"    goal{gi} id cap={cg.GoalId} got={bg.GoalId} | n cap={cg.N} got={bg.N} | k cap={cg.K} got={bg.K} | " +
						            $"sub cap={cg.SubId} got={bg.SubId} | map cap={cg.MapId} got={bg.MapId} | req cap={cg.RequiredCount} got={bg.RequiredCount} | " +
						            $"marker cap=({cg.MarkerX},{cg.MarkerY}) got=({bg.MarkerX},{bg.MarkerY})");
						o.WriteLine($"      rest cap: lead={cg.Lead} rep={cg.GoalIdRepeat} inst={cg.Instance} z1={cg.Unknown1} z2={cg.Unknown2} res=[{string.Join(",", cg.Reserved)}]");
						o.WriteLine($"      rest got: lead={bg.Lead} rep={bg.GoalIdRepeat} inst={bg.Instance} z1={bg.Unknown1} z2={bg.Unknown2} res=[{string.Join(",", bg.Reserved)}]");
						if (!cg.Objective.SequenceEqual(bg.Objective))
							o.WriteLine($"      OBJ cap={Encoding.Latin1.GetString(cg.Objective)}\n      OBJ got={Encoding.Latin1.GetString(bg.Objective)}");
						if (!cg.TextRecord.SequenceEqual(bg.TextRecord))
							o.WriteLine($"      TXT cap={Encoding.Latin1.GetString(cg.TextRecord)}\n      TXT got={Encoding.Latin1.GetString(bg.TextRecord)}");
					}
				}
			}

			// For each goal id, how do the captures spell its trailing record?
			var spell = new Dictionary<uint, (int Localized, int Remote)>();
			foreach (var (_, sub2, _) in CompletedQuestJournalTests.SubClassesForProbe())
			foreach (var rec2 in sub2.Quests.Records)
			{
				if (rec2.Objectives is not { Goals.Count: > 0 } o2)
					continue;

				foreach (var g2 in o2.Goals)
				{
					var txt = Encoding.Latin1.GetString(g2.TextRecord);
					var cur = spell.GetValueOrDefault(g2.GoalId);
					spell[g2.GoalId] = txt.Contains("<localized")
						? (cur.Localized + 1, cur.Remote)
						: (cur.Localized, cur.Remote + 1);
				}
			}

			o.WriteLine($"goals seen={spell.Count}  always-localized={spell.Count(k => k.Value.Remote == 0)}  " +
			            $"always-remoteformat={spell.Count(k => k.Value.Localized == 0)}  MIXED={spell.Count(k => k.Value is { Localized: > 0, Remote: > 0 })}");
			foreach (var k in spell.Where(x => x.Key is 1448 or 3490))
				o.WriteLine($"  goal {k.Key}: localized={k.Value.Localized} remoteformat={k.Value.Remote}");

			// Do two captures of the SAME goal agree on the reserved tags and the text spelling? If not, byte-exact
			// against every capture is impossible by construction and the gate must target the harvested source.
			var tagVals = new Dictionary<uint, HashSet<string>>();
			foreach (var (_, sub3, _) in CompletedQuestJournalTests.SubClassesForProbe())
			foreach (var rec3 in sub3.Quests.Records)
			{
				if (rec3.Objectives is not { Goals.Count: > 0 } o3)
					continue;

				foreach (var g3 in o3.Goals)
				{
					var sig = $"{g3.Unknown1}|{g3.Unknown2}|{string.Join(",", g3.Reserved)}|{Encoding.Latin1.GetString(g3.TextRecord)}";
					(tagVals.TryGetValue(g3.GoalId, out var set)
						? set
						: tagVals[g3.GoalId] = []).Add(sig);
				}
			}

			o.WriteLine($"goals seen across captures={tagVals.Count}; goals whose zero1/zero2/reserved/text VARY: {tagVals.Count(k => k.Value.Count > 1)}");
			var z1 = new Dictionary<uint, HashSet<uint>>();
			foreach (var (_, sub4, _) in CompletedQuestJournalTests.SubClassesForProbe())
			foreach (var rec4 in sub4.Quests.Records)
			{
				if (rec4.Objectives is not { Goals.Count: > 0 } o4)
					continue;

				foreach (var g4 in o4.Goals)
					(z1.TryGetValue(g4.GoalId, out var st)
						? st
						: z1[g4.GoalId] = []).Add(g4.Unknown1);
			}

			o.WriteLine($"  Zero1: nonzero on {z1.Count(k => k.Value.Any(v => v != 0))} of {z1.Count} goals; VARIES across captures on {z1.Count(k => k.Value.Count > 1)}");

			// NecroL80's snapshot-eligible quests specifically - these are what the server would actually ship.
			uint[] necrol80 = [17624, 16324, 13784, 17171, 16341, 17238, 17236, 17168, 19365];
			foreach (var q in necrol80)
				o.WriteLine($"  NECROL80 quest {q}: {(okByQuest.Contains(q) ? "OK" : failByQuest.ContainsKey(q) ? "FAILS" : "not in corpus")}");

			// What IS GoalIdRepeat / Zero2 / Reserved, when they are not what we assume?
			int rptEqualsGoal = 0, rptEqualsQuest = 0, rptOther = 0, z2NonZero = 0, allGoals = 0;
			var rptSamples = new List<string>();
			var stableRpt = new Dictionary<uint, HashSet<uint>>();
			foreach (var (_, sub5, _) in CompletedQuestJournalTests.SubClassesForProbe())
			foreach (var rec5 in sub5.Quests.Records)
			{
				if (rec5.Objectives is not { Goals.Count: > 0 } o5)
					continue;

				foreach (var g5 in o5.Goals)
				{
					allGoals++;
					if (g5.GoalIdRepeat == g5.GoalId)
						rptEqualsGoal++;
					else if (g5.GoalIdRepeat == rec5.QuestId)
						rptEqualsQuest++;
					else
					{
						rptOther++;
						if (rptSamples.Count < 6)
							rptSamples.Add($"quest {rec5.QuestId} goal {g5.GoalId}: repeat={g5.GoalIdRepeat} n={g5.N} k={g5.K} req={g5.RequiredCount} z2={g5.Unknown2}");
					}

					if (g5.Unknown2 != 0)
						z2NonZero++;
					(stableRpt.TryGetValue(g5.GoalId, out var st5)
						? st5
						: stableRpt[g5.GoalId] = []).Add(g5.GoalIdRepeat);
				}
			}

			o.WriteLine(
				$"GoalIdRepeat: ==GoalId {rptEqualsGoal}, ==QuestId {rptEqualsQuest}, other {rptOther} (of {allGoals}); VARIES per goal on {stableRpt.Count(k => k.Value.Count > 1)} of {stableRpt.Count}");
			o.WriteLine($"Zero2 nonzero on {z2NonZero} goals");
			foreach (var x in rptSamples)
				o.WriteLine($"    {x}");

			// Are Unknown1/Unknown2 really u32s, or a byte pair the client writes separately?
			uint u1max = 0, u2max = 0;
			int u1over = 0, u2over = 0, seenG = 0;
			var u1vals = new HashSet<uint>();
			var u2vals = new HashSet<uint>();
			foreach (var (_, sub6, _) in CompletedQuestJournalTests.SubClassesForProbe())
			foreach (var rec6 in sub6.Quests.Records)
			{
				if (rec6.Objectives is not { Goals.Count: > 0 } o6)
					continue;

				foreach (var g6 in o6.Goals)
				{
					seenG++;
					u1max = Math.Max(u1max, g6.Unknown1);
					u2max = Math.Max(u2max, g6.Unknown2);
					if (g6.Unknown1 > 0xFF)
						u1over++;
					if (g6.Unknown2 > 0xFF)
						u2over++;
					u1vals.Add(g6.Unknown1);
					u2vals.Add(g6.Unknown2);
				}
			}

			o.WriteLine($"BYTES? goals={seenG} Unknown1 max={u1max} over255={u1over} distinct={u1vals.Count} [{string.Join(",", u1vals.OrderBy(v => v).Take(10))}]");
			o.WriteLine($"BYTES? Unknown2 max={u2max} over255={u2over} distinct={u2vals.Count} [{string.Join(",", u2vals.OrderBy(v => v).Take(10))}]");

			foreach (var kv in fieldDiffs.OrderByDescending(k => k.Value))
				o.WriteLine($"    DIFF {kv.Key,-18} {kv.Value}");

			o.WriteLine($"objective regions compared={compared} byteExact={exact} skipped={skipped}");
			o.WriteLine($"  failing: {failByQuest.Count} distinct quests; ok: {okByQuest.Count} distinct quests");
			o.WriteLine($"  length differs={lenDiff} sameLengthButDiffers={sameLenDiff}");
			if (first.Length > 0)
				o.WriteLine($"  {first}");

			// The hard gate is on what we SHIP: a quest flagged ObjectiveRegionVerified must rebuild byte-exact in
			// every capture it appears in. Overall coverage is reported, not asserted - the region still has undecoded
			// fields, and the flag is what keeps the unreproducible ones out of the login snapshot.
			var shipped = defs.Values.Where(d => d.ObjectiveRegionVerified).Select(d => d.QuestId).ToHashSet();
			var shippedButFailing = failByQuest.Keys.Where(shipped.Contains).ToList();
			o.WriteLine($"  quests flagged verified={shipped.Count}; of those FAILING here={shippedButFailing.Count}");

			Assert.True(compared > 0, "no captured objective region could be rebuilt");
			Assert.True(shippedButFailing.Count == 0, $"quests flagged ObjectiveRegionVerified must rebuild byte-exact, but these do not: {string.Join(",", shippedButFailing.Take(10))}");
		}

		[Fact]
		public async Task CapturedQuestIndexes_RebuildFromTheDatabase_ByteExact()
		{
			await using var db = new DatabaseContext(TestDb.Options(TestPaths.FaolanDb));
			var defs = await db.QuestDefinitions.ToDictionaryAsync(q => q.QuestId, TestContext.Current.CancellationToken);
			var areaRows = await db.QuestAreas.ToDictionaryAsync(a => a.AreaId, TestContext.Current.CancellationToken);
			var catalogs = (await db.QuestDefinitions.Where(q => q.AreaId != 0).ToListAsync(TestContext.Current.CancellationToken))
				.GroupBy(q => q.AreaId)
				.ToDictionary(g => g.Key, g => (IReadOnlyList<QuestDefinition>)[.. g.OrderBy(q => q.JournalSequence)]);

			int compared = 0, exact = 0, notGeneratable = 0;
			var first = "";

			foreach (var (source, sub, _) in CompletedQuestJournalTests.SubClassesForProbe())
			{
				if (sub.QuestIndex is not { } captured || captured.QuestIds.Count == 0)
					continue;

				// Since #391 the index parses for EVERY captured journal, including multi-area ones whose per-quest
				// sequences and trailer words are NOT modelled in the DB (they are carried on the wire, not stored).
				// QuestIndexRegion.Build can only reproduce the "simple" beach shape - monotonic 1..n area sequences and
				// a (1,0,0) block trailer - so restrict the byte-exact gate to those; the rest are generation follow-up
				// (#389), not a builder defect. Their PARSE round-trip is still guarded by the corpus round-trip gate.
				bool Generatable(QuestIndexRegion.Block b)
				{
					return b is { TrailerA: 1, TrailerB: 0, TrailerC: 0 } && (b.AreaQuestSequences.Count == 0 || b.AreaQuestSequences.Select((s, i) => s == (uint)(i + 1)).All(x => x));
				}

				// Multi-block (multi-quest) indexes are DB-generation follow-up (#389): the per-quest chain of leadArea
				// ids and the area-catalog cross-references are only partially modelled, so Build does not yet reproduce
				// them byte-exact even when each block's shape is generatable. Their PARSE round-trip is guarded by the
				// corpus round-trip gate; here we hold the line on the single-block indexes the beach path actually
				// ships. (The 2-quest beach+Casilda generation is proven separately by BuildQuestPortionTests.)
				if (captured.Blocks.Count != 1 || !captured.Blocks.All(Generatable))
				{
					notGeneratable++;
					continue;
				}

				// Rebuild from the SAME quests, in the capture's order.
				var ordered = captured.QuestIds.Select(defs.GetValueOrDefault).ToList();
				if (ordered.Any(d => d == null))
					continue; // catalog gap, not a builder failure

				// Build reads the area's magic + full quest catalog from the DB. Where the importer has not harvested
				// those (an area only ever seen through a character's ACTIVE quests), Build cannot reproduce the block -
				// a DATA gap tracked by #389, not a builder defect. Skip an index unless the DB carries, for every
				// block, the matching area magic and a catalog of the captured size.
				bool DbComplete()
				{
					foreach (var blk in captured.Blocks)
					{
						var def = defs.GetValueOrDefault(blk.TrailerQuestId);
						if (def == null || !areaRows.TryGetValue(def.AreaId, out var area) || area.Magic != blk.Magic)
							return false;
						if (!catalogs.TryGetValue(def.AreaId, out var cat) || cat.Count != blk.AreaQuestIds.Count)
							return false;
					}

					return true;
				}

				if (!DbComplete())
				{
					notGeneratable++;
					continue;
				}

				var built = QuestIndexRegion.Build([.. ordered!], ordered[0]!, areaRows, catalogs).ToArray();
				var want = captured.ToArray();
				compared++;
				if (built.SequenceEqual(want))
				{
					exact++;
					continue;
				}

				if (first.Length == 0)
				{
					var b = QuestIndexRegion.Parse(built, 0, built.Length);
					first = $"{source}: {built.Length} B built vs {want.Length} B captured; " + $"block0 tf1={b.Blocks[0].TableField1}/{captured.Blocks[0].TableField1} " +
					        $"tf2={b.Blocks[0].TableField2}/{captured.Blocks[0].TableField2} " + $"chain=0x{b.Blocks[0].ChainWord:X}/0x{captured.Blocks[0].ChainWord:X} " +
					        $"areaQuests={b.Blocks[0].AreaQuestIds.Count}/{captured.Blocks[0].AreaQuestIds.Count}";
				}
			}

			o.WriteLine($"quest indexes compared={compared} byteExact={exact} notGeneratable(multi-area, #389)={notGeneratable}");
			if (first.Length > 0)
				o.WriteLine($"  {first}");

			Assert.True(compared > 0, "no captured quest index could be rebuilt");
			Assert.Equal(compared, exact);
		}

		[Fact]
		public async Task CapturedRegions_RebuildFromTheDatabase_ByteExact()
		{
			await using var db = new DatabaseContext(TestDb.Options(TestPaths.FaolanDb));

			var defs = await db.QuestDefinitions.Include(q => q.TextRefs).ToDictionaryAsync(q => q.QuestId, TestContext.Current.CancellationToken);
			var areas = await db.QuestAreas.ToDictionaryAsync(a => a.AreaId, TestContext.Current.CancellationToken);
			o.WriteLine($"catalog: {defs.Count} quest definitions, {areas.Count} areas");

			int regions = 0, rebuilt = 0, skippedUnknown = 0, mismatched = 0;
			var firstMismatch = "";

			foreach (var (source, region) in CompletedQuestJournalTests.CapturedRegions())
			{
				regions++;

				// Only regions whose every area AND quest we actually hold can be rebuilt; anything else would be
				// testing the harvest's coverage rather than the builder.
				var groups = new List<(QuestArea, IReadOnlyList<QuestDefinition>)>();
				var complete = true;
				foreach (var g in region.Areas)
				{
					if (!areas.TryGetValue(g.AreaId, out var area) || !g.Quests.All(q => defs.ContainsKey(q.QuestId)))
					{
						complete = false;
						break;
					}

					groups.Add((area, [.. g.Quests.Select(q => defs[q.QuestId])]));
				}

				if (!complete)
				{
					skippedUnknown++;
					continue;
				}

				var built = CompletedQuestJournalRegion.Build(groups).ToArray();
				var expected = region.ToArray();
				if (built.SequenceEqual(expected))
				{
					rebuilt++;
					continue;
				}

				mismatched++;
				if (firstMismatch.Length > 0)
					continue;

				// Report the first differing AREA, not just a byte offset - the byte offset says nothing about which
				// field is wrong, and every failure on the way here was a single field (map/region order, then the
				// per-quest sequence word).
				for (var a = 0; a < region.Areas.Count; a++)
				{
					var cap = new CompletedQuestJournalRegion
					{
						Areas = [region.Areas[a]]
					}.ToArray();
					var got = CompletedQuestJournalRegion.Build([groups[a]]).ToArray();
					if (cap.SequenceEqual(got))
						continue;

					var g = region.Areas[a];
					firstMismatch = $"{source}: area[{a}] id={g.AreaId} quests={g.Quests.Count} " + $"magic=0x{g.Magic:X8} seqs=[{string.Join(",", g.Quests.Take(5).Select(q => q.Sequence))}] " +
					                $"({cap.Length} B captured vs {got.Length} B built)";
					break;
				}
			}

			o.WriteLine($"regions={regions} rebuiltByteExact={rebuilt} mismatched={mismatched} skipped(catalog gaps)={skippedUnknown}");
			if (firstMismatch.Length > 0)
				o.WriteLine($"  {firstMismatch}");

			Assert.True(rebuilt > 0, "no captured region could be rebuilt from the database");
			Assert.Equal(0, mismatched);
		}

		// The completed journal is only emitted ALONGSIDE a non-empty active journal, because that is the only shape
		// the corpus shows: 81 captured completed journals, every one paired with active quests, and 0 counter-
		// examples in 104 captures.
		//
		// This is pinned as a test because emitting the other shape looks like an obvious improvement - a character
		// with 1511 completed and 0 eligible active quests plainly "should" get its Completed tab - and doing it
		// crashed the client on statlist.cpp:134. Byte-exact generation proves the bytes; it says nothing about
		// whether the client accepts the SHAPE they sit in.
		[Fact]
		public void CompletedJournal_IsNotEmitted_WithoutActiveQuests()
		{
			var area = new QuestArea
			{
				AreaId = 241,
				Magic = 0x57365154,
				AreaTitleKey = "k",
				MapKey = "k",
				RegionKey = "k"
			};
			var quest = new QuestDefinition
			{
				QuestId = 10238,
				AreaId = 241,
				JournalSequence = 1
			};
			var groups = new List<(QuestArea, IReadOnlyList<QuestDefinition>)>
			{
				(area, [quest])
			};

			Assert.Equal(FullCharSnapshotSubClass.BuildEmptyQuestPortion(), FullCharSnapshotSubClass.BuildQuestPortion([], null!, 1, groups));
		}

		// The quest INDEX, rebuilt from DB rows and compared byte-for-byte with the capture. This is the gate that
		// was missing: the index was generated from QuestDefinition columns that are 0 for every harvested quest
		// (tf1/tf2/trailer), so the server shipped a malformed index and the client died in StatList_t - while every
		// existing test stayed green, because none of them regenerated an index.
		private static void Bump(Dictionary<string, int> d, string k)
		{
			d[k] = d.GetValueOrDefault(k) + 1;
		}
	}
}
