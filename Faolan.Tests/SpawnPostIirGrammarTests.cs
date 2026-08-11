using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The NPC post-IIR stream and the PLAYER post-IIR stream are the same grammar (Gitea #475).
	//
	// They were modelled as two unrelated things: `PlayerSpawnScaffoldCodec` wrote a `[u32 4][u32 2]` pair as bare
	// constants, and `NpcSpawnPostIir` read the same two words as `[firstRecordLen][recordCount]` and kept
	// everything after them as an opaque `byte[] Records`. Both are the same header, and what follows it is the
	// same stream apart from one slot:
	//
	//   [u32 headCount][headCount x u32] [u32 firstRecordLen] [u32 word2]
	//   [firstRecordLen bytes - the first record, ENDING in the buff list's framing word]
	//   [buffs] [the entry slot] [pair list][pair list][ListFrame-counted pair list]
	//
	// The player's "4" is its first record's length and its "2" is word2; an NPC's are 5 and 1/2/3. So neither is a
	// protocol constant - they are only constant for the player, which is exactly how they came to be literals.
	public class SpawnPostIirGrammarTests
	{
		// The MINIMAL forms, whose entry slot and pair lists are all empty: what is being pinned here is the header
		// the two codecs used to disagree about, and the fact that one reader now covers both families.
		[Fact]
		public void TheNpcAndPlayerPostIirStreams_AreTheSameGrammar()
		{
			var shapes = new Dictionary<string, int>();

			foreach (var (body, post) in Streams())
			{
				if (!SpawnBodyReader.TryParse(body, out var spawn) || MorphsOf(spawn).Count > 0)
					continue;
				if (spawn.Post.BuffRecords.Count > 0 || spawn.Post.EndPairs.Count > 0 || spawn.Post.EndPairs2.Count > 0 || spawn.Post.EndPairs3.Count > 0)
					continue;

				var shape = $"len={post.Length} firstRecordLen={spawn.Post.FirstRecordLen} word2={spawn.Post.MovementMode}";
				shapes[shape] = shapes.GetValueOrDefault(shape) + 1;
			}

			// Pinned to the FULL corpus. The 40-byte form is the create-editor preview PLAYER scaffold, fully empty;
			// the 41-byte one is the NPC minimal form, which differs by exactly one leading byte in its first record.
			// That one byte, and nothing else, is what made them look like different formats.
			Assert.Equal(21, shapes["len=40 firstRecordLen=4 word2=2"]);
			Assert.Equal(40, 12 + 4 + 24); // the empty-tail identity, spelled out for the two minimal forms
			Assert.Equal(41, 12 + 5 + 24);
			Assert.Equal(1609 + 3513 + 30, shapes.Where(s => s.Key.StartsWith("len=41 ")).Sum(s => s.Value));
		}

		// word2 is NOT a record count, which is what it was called until this landed: it takes three different values
		// across streams of IDENTICAL length, so it cannot be counting anything that occupies bytes. This is the gate
		// behind the SpawnPostIir.MovementMode rename.
		[Fact]
		public void Word2_DoesNotAffectTheStreamsLength_SoItIsNotARecordCount()
		{
			var byLength = new Dictionary<int, HashSet<uint>>();

			foreach (var (body, post) in Streams())
			{
				if (!SpawnBodyReader.TryParse(body, out var spawn) || MorphsOf(spawn).Count > 0)
					continue;

				byLength.TryAdd(post.Length, []);
				byLength[post.Length].Add(spawn.Post.MovementMode);
			}

			Assert.Equal([1u, 2u, 3u], byLength[41].Order().ToArray());
			Assert.Equal([2u], byLength[40].ToArray());
		}

		// ⭐ #466 item 3: the THIRD closing pair list is a STAT list, and that is why it is framed differently from
		// the two before it. Lists 1 and 2 are the faction-reputation block (#315) - ids in the decades
		// 10..120 plus 200/310/320/330, values a signed standing. List 3's ids are wire STAT ids, and every one
		// resolves in the client's own stat-name table (docs/re/aoc-client-stat-names.tsv):
		//
		//     20      BCCState                    x8   value 1        NPC spawns
		//     975     DamageAbsorb                x39  value 0        both families
		//     1351    NaturalEnergyRegeneration   x9   5625 / 7368    PLAYER spawns only
		//     1000028 Energy                      x9   3222/9000/14000  PLAYER spawns only
		//     1000029 MaxEnergy                   x9   9000 / 14000   PLAYER spawns only
		//
		// The three energy stats appear on exactly the same 9 spawns, which is what makes them a deliberate triple
		// rather than noise: a pool, its denominator and its regen rate, pushed together, with Energy <= MaxEnergy
		// on every one. #466 recorded this list as "(975, 0) on 10 spawns, always identical" - that is the
		// player-scaffold subset; over the whole corpus it is 56 carriers and five ids.
		//
		// ⚠️ This also corrects a standing claim that `MaxEnergy` (0x000F425D) "appears ZERO times in 120 captures"
		// and that the bytes are a leaked scaffold region rather than a value retail wrote. They are this list -
		// count-framed, parsed by our own codec, and round-tripped byte-exact.
		[Fact]
		public void TheThirdPairList_IsAStatList_NotAThirdFactionList()
		{
			var factionIds = new HashSet<uint>();
			var thirdIds = new HashSet<uint>();
			var energySpawns = new Dictionary<uint, int>();
			var pools = new List<(int Energy, int MaxEnergy)>();
			var carriers = 0;

			foreach (var (body, _) in Streams())
			{
				if (!SpawnBodyReader.TryParse(body, out var spawn))
					continue;

				foreach (var pair in spawn.Post.EndPairs.Concat(spawn.Post.EndPairs2))
					factionIds.Add(pair.Id);

				if (spawn.Post.EndPairs3.Count == 0)
					continue;

				carriers++;
				foreach (var pair in spawn.Post.EndPairs3)
				{
					thirdIds.Add(pair.Id);
					if (pair.Id is EnergyStat or MaxEnergyStat or EnergyRegenStat)
						energySpawns[pair.Id] = energySpawns.GetValueOrDefault(pair.Id) + 1;
				}

				var energy = spawn.Post.EndPairs3.Where(p => p.Id == EnergyStat).Select(p => (int?)p.Value).FirstOrDefault();
				var max = spawn.Post.EndPairs3.Where(p => p.Id == MaxEnergyStat).Select(p => (int?)p.Value).FirstOrDefault();
				if (energy is { } e && max is { } m)
					pools.Add((e, m));
			}

			Assert.Equal(56, carriers);
			Assert.Equal([20u, 975u, 1351u, EnergyStat, MaxEnergyStat], thirdIds.Order().ToArray());

			// The ids are NOT the faction domain: the four above 120 cannot be faction ids, and the one that does
			// collide (20) is `BCCState`, which is a small-number coincidence rather than an overlap of families.
			Assert.Equal([20u], thirdIds.Intersect(factionIds).ToArray());
			Assert.All(thirdIds.Where(id => id != 20), id => Assert.True(id > factionIds.Max(), $"{id} sits inside the faction range"));

			// ⭐ The energy triple is pushed TOGETHER, on the same spawns, and it is a real pool.
			Assert.Equal(energySpawns[EnergyStat], energySpawns[MaxEnergyStat]);
			Assert.Equal(energySpawns[EnergyStat], energySpawns[EnergyRegenStat]);
			Assert.NotEmpty(pools);
			Assert.All(pools, p => Assert.True(p.Energy <= p.MaxEnergy, $"Energy {p.Energy} exceeds MaxEnergy {p.MaxEnergy}"));
		}

		// The three energy stats, by the client's own names (docs/re/aoc-client-stat-names.tsv).
		private const uint EnergyStat = 1000028;
		private const uint MaxEnergyStat = 1000029;
		private const uint EnergyRegenStat = 1351;

		private static List<NpcAppearanceMorph> MorphsOf(SimpleCharFullUpdateIIR spawn)
		{
			return spawn.Post.Slot is NpcEntrySlot npc
				? npc.Morphs
				: [];
		}

		// Every captured FA015807's post-IIR stream, sliced off behind the embedded CharacterIIR, paired with the
		// body it came from (the codec reads the whole record, so the family is decided there rather than here).
		private static IEnumerable<(byte[] Body, byte[] Post)> Streams()
		{
			foreach (var (_, body) in CorpusExtract.OfType((uint)IirTypes.SimpleCharFullUpdateIIR))
				if (SpawnBodyReader.TryReadCharacterMessage(body, out var read))
					yield return (body, body[(read.Offset + read.Length)..]);
		}
	}
}
