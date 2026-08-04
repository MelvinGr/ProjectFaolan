using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Progression;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// #297: the XP curve is SERVER data - the client ships none of it (no XP table among the 25 named CoreTables data
	// tables, no level-threshold entry in GAMETWEAKS, and the exe's only XP strings are the three UI value names it
	// fills from these stats). So every number in LevelingResolver has to come off the wire, and this is the gate that
	// says it does: the resolver is replayed against every XP triple the corpus carries.
	//
	// It is also the gate that would have caught the bug it was written for. The anchors were harvested as MaxExp
	// values and then handed back as PER-LEVEL costs, which are two different quantities: MaxExp is the running total
	// at which the NEXT level begins, so level 24 "cost" 298,040 XP instead of its real 34,400.
	public class LevelingCurveCorpusTests
	{
		// Every distinct (Level, ExpCurrentLevelBase, MaxExp) the captured snapshots carry. The stat sheet addresses
		// by id, so the triple is read by id rather than by slot.
		private static IReadOnlyList<(uint Level, uint Base, uint Max)> CapturedTriples()
		{
			var rows = new HashSet<(uint, uint, uint)>();

			foreach (var (_, sub, _) in CompletedQuestJournalTests.SubClassesForProbe())
			{
				SubClassPostAccessBlock parsed;
				try
				{
					parsed = SubClassPostAccessBlockCodec.Parse(sub.UnknownAfterAccessWindowTable ?? []);
				}
				catch
				{
					continue; // not a post-access block (create-editor subclasses and the like)
				}

				var pairs = parsed.Displayed.Pairs.Concat(parsed.Sheet.Pairs).ToList();

				uint? Value(CharacterStatId id)
				{
					foreach (var (statId, value) in pairs)
					{
						if (statId == (uint)id)
							return value;
					}

					return null;
				}

				if (Value(CharacterStatId.Level) is { } level && Value(CharacterStatId.ExpCurrentLevelBase) is { } levelBase && Value(CharacterStatId.MaxExp) is { } max)
					rows.Add((level, levelBase, max));
			}

			return [.. rows.OrderBy(r => r.Item1)];
		}

		// The load-bearing claim: for every captured character, the base IS the threshold of its level and MaxExp IS
		// the threshold of the next one. If the two were per-level costs instead, none of this would hold.
		[Fact]
		public void TheResolverReproducesEveryCapturedXpTriple()
		{
			var triples = CapturedTriples();
			Assert.True(triples.Count >= 5, $"the corpus should carry several XP triples, found {triples.Count}");

			foreach (var (level, levelBase, max) in triples)
			{
				Assert.Equal(levelBase, LevelingResolver.CumulativeXpForLevel(level));
				Assert.Equal(max, LevelingResolver.CumulativeXpForLevel(level + 1));
				Assert.Equal(max - levelBase, LevelingResolver.XpForNextLevel(level));
			}
		}

		// Two adjacent captured levels pin the semantics on their own: a level-23 character's MaxExp is a level-24
		// one's base. A cost table cannot produce that; a threshold table cannot avoid it.
		[Fact]
		public void OneLevelsMaxExp_IsTheNextLevelsBase()
		{
			var byLevel = CapturedTriples().GroupBy(t => t.Level).ToDictionary(g => g.Key, g => g.First());
			var adjacent = byLevel.Keys.Where(l => byLevel.ContainsKey(l + 1)).ToList();

			Assert.NotEmpty(adjacent);
			foreach (var level in adjacent)
				Assert.Equal(byLevel[level].Max, byLevel[level + 1].Base);
		}

		// NewLevelIIR's six "deltas" were documented as undecoded. Three of them are this same triple: the ding
		// carries [Exp, ExpCurrentLevelBase, MaxExp, 1, 8, 0], where only the first varies between players who ding
		// to the same level (the L2 ding shows 100 / 120 / 168 across captures, with 100 and 340 fixed behind it).
		[Fact]
		public void TheLevelUpDings_CarryTheSameThresholdsInDeltas1And2()
		{
			var dings = new List<NewLevelIIR>();
			foreach (var (_, body) in CorpusExtract.OfType((uint)IirTypes.NewLevelIIR))
			{
				try
				{
					dings.Add(NewLevelCodec.Parse(body));
				}
				catch
				{
					// not a standalone NewLevelIIR body
				}
			}

			foreach (var ding in dings)
			{
				Assert.Equal(LevelingResolver.CumulativeXpForLevel(ding.NewLevel), ding.Deltas[1]);
				Assert.Equal(LevelingResolver.CumulativeXpForLevel(ding.NewLevel + 1), ding.Deltas[2]);

				// deltas[0] is the player's own running total, which must sit inside the level it just reached.
				Assert.InRange(ding.Deltas[0], ding.Deltas[1], ding.Deltas[2]);
			}
		}
	}
}
