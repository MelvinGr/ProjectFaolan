using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Importer.Rdb;
using Xunit;

namespace Faolan.Tests
{
	// Gates for the bounded-area naming survey (BoundedAreaClassifier, #513) - the buckets
	// docs/rdb/bounded-areas-1000053.md's census is organised by.
	//
	// The classifier is ORDERED and first-match-wins, so what needs gating is not "does `lava` match lava" but the
	// ORDERING: every name below is a real region from the corpus that two rules both match, and the assertion is which
	// rule is allowed to win. Those are the cases that silently mis-sort a whole family.
	public class BoundedAreaClassifierTests
	{
		// One real corpus name per bucket. Doubles as the reachability gate below: a bucket with no sample fails, which
		// is what stops a bucket being declared with a rule that can never match (a census row reading 0 says "the
		// client ships none of these", the opposite of "we could not describe them").
		private static readonly (BoundedAreaBucket Bucket, string Name)[] Representatives =
		[
			(BoundedAreaBucket.Collision, "BoundedAreaCollision7"),
			(BoundedAreaBucket.Audio, "BoundedAreaSFX_Fixed3D0"),
			(BoundedAreaBucket.Resurrection, "BA_Resurrection_TheOutcastCamp"),
			(BoundedAreaBucket.Teleport, "TeleportToArena"),
			(BoundedAreaBucket.Dismount, "DismountArea1"),
			(BoundedAreaBucket.HazardLava, "BA_Lava7"),
			(BoundedAreaBucket.HazardTrap, "BA_SpikeTrap"),
			(BoundedAreaBucket.HazardDrown, "PlayerDrowns_Dies"),
			(BoundedAreaBucket.HazardFireDeath, "BA_PoisonFog"),
			(BoundedAreaBucket.QuestGoal, "BA_QuestSpawn_TheDevilsLight"),
			(BoundedAreaBucket.Encounter, "TacticianCampAmbush"),
			(BoundedAreaBucket.SpawnCamp, "Camp18 Mammoths"),
			(BoundedAreaBucket.Uncategorised, "Chosen_Location_1")
		];

		[Theory]
		[MemberData(nameof(RepresentativeCases))]
		public void RepresentativeNames_LandInTheirBucket(BoundedAreaBucket expected, string name)
		{
			Assert.Equal(expected, BoundedAreaClassifier.Classify(name));
		}

		public static IEnumerable<object[]> RepresentativeCases()
		{
			return Representatives.Select(r => new object[] { r.Bucket, r.Name });
		}

		// Every declared bucket must be reachable, and Order must list every one of them - the census iterates Order, so
		// a bucket missing from it would be counted into nothing and simply vanish from the table.
		[Fact]
		public void EveryBucket_IsOrderedAndReachable()
		{
			var declared = Enum.GetValues<BoundedAreaBucket>();
			Assert.Equal(declared.Length, BoundedAreaClassifier.Order.Count);
			Assert.Equal(declared.ToHashSet(), BoundedAreaClassifier.Order.ToHashSet());

			foreach (var bucket in declared)
				Assert.Contains(bucket, Representatives.Select(r => r.Bucket));
		}

		// ⚠️ The ordering cases. Each of these names matches TWO rules, and the wrong winner mis-sorts a family:
		//
		// The audio TWIN of a gameplay region must not shadow the region it is named after - the same distinction
		// RdbHazardImporter draws when it refuses `BA_Lava_music` as a damage volume.
		[Theory]
		[InlineData("BA_Lava_music", BoundedAreaBucket.Audio)] // not HazardLava
		[InlineData("Vanir_camp_music", BoundedAreaBucket.Audio)] // not SpawnCamp
		[InlineData("Vanir_camp_music0", BoundedAreaBucket.Audio)]
		// "spawn" is in despawn triggers, collision names and quest hooks alike; it is the WEAKEST camp signal and every
		// stronger claim on the name wins first.
		[InlineData("SpawnAreaBlocker1", BoundedAreaBucket.Collision)]
		[InlineData("Boss Spawn Collision 1", BoundedAreaBucket.Collision)]
		[InlineData("BossSpawnTrigger", BoundedAreaBucket.Encounter)]
		[InlineData("DespawnValeria", BoundedAreaBucket.Encounter)]
		[InlineData("SwampDemonRingSpawnTrigger", BoundedAreaBucket.Encounter)]
		[InlineData("BA_QuestSpawn_TheWorkofaDeadMan", BoundedAreaBucket.QuestGoal)]
		// A resurrection pad named after the camp it serves is a resurrection point, not a camp.
		[InlineData("BA_Resurrection_TheOutcastCamp", BoundedAreaBucket.Resurrection)]
		[InlineData("zang_xin_camp_rezpad", BoundedAreaBucket.Resurrection)]
		[InlineData("ShadyCampRezz", BoundedAreaBucket.Resurrection)]
		// An ambush trigger inside a camp is a trigger first.
		[InlineData("TacticianCampAmbush", BoundedAreaBucket.Encounter)]
		[InlineData("BA_DR_Camp5cBossfight", BoundedAreaBucket.Encounter)]
		// ...and what is left saying "camp" is a mob camp.
		[InlineData("Camp23 White Hands", BoundedAreaBucket.SpawnCamp)]
		[InlineData("Camp 13 Atzels Bandits (Logger Camp)", BoundedAreaBucket.SpawnCamp)]
		[InlineData("Outlaw Camp", BoundedAreaBucket.SpawnCamp)]
		[InlineData("TroglodyteCamp", BoundedAreaBucket.SpawnCamp)]
		[InlineData("Mob spawn center 1", BoundedAreaBucket.SpawnCamp)]
		public void ContestedNames_GoToTheStrongerClaim(string name, BoundedAreaBucket expected)
		{
			Assert.Equal(expected, BoundedAreaClassifier.Classify(name));
		}

		// The slug is what `--report-arg` takes, so it has to round-trip for every bucket.
		[Fact]
		public void Slugs_RoundTrip()
		{
			foreach (var bucket in BoundedAreaClassifier.Order)
			{
				var slug = BoundedAreaClassifier.Slug(bucket);
				Assert.Equal(slug, slug.ToLowerInvariant());
				Assert.Equal(bucket, BoundedAreaClassifier.FromSlug(slug));
			}

			Assert.Equal(BoundedAreaBucket.HazardLava, BoundedAreaClassifier.FromSlug("hazard-lava"));
			Assert.Null(BoundedAreaClassifier.FromSlug("not-a-bucket"));
		}
	}
}
