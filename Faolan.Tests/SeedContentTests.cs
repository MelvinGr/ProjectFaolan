using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Importer.Rdb;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// The shipped seed actually CARRIES its content.
	//
	// Written after a schema migration silently emptied a table and the whole suite still passed: rebuilding a
	// parent table dropped it while a child still referenced it ON DELETE CASCADE, so all 782 shop wares were
	// deleted before the rebuild copied them, and nothing noticed. Every gate we had asserts that content is
	// CORRECT; none asserted that it is THERE. This is the missing one - a floor under each table the game is
	// unplayable without, so a destructive migration or a broken import fails loudly instead of shipping an empty
	// world.
	//
	// The floors are deliberately well below the current counts: this is a canary for catastrophic loss, not a
	// ratchet that fights every legitimate data change. Runs against the fixture built from faolan.sql.
	public class SeedContentTests
	{
		// Conarch Armory - the class-specific instance behind Conarch Village's `Armory` door, and the only one any
		// capture has walked into (Hakotep as a Herald of Xotli, NecroL80 as a Necromancer).
		private const uint ConarchArmoryMapId = 5007;

		// #703: the harvester's own leg-join tolerance (PatrolPathExtractor.LegJoinTolerance) - legs that are one beat
		// join within this, a leg that belongs elsewhere misses by far more.
		private const float PatrolLegJoinTolerance = 5f;

		// A reach objective needs somewhere to walk to. The runtime solves one by proximity to the goal's own map
		// MARKER, so a ReachArea goal with no marker can never be completed - and a quest that cannot be completed is
		// strictly worse than one that is visibly inert, because nothing says so.
		//
		// This gate exists because the rule that types these got it wrong first (#641). `MapId`/`MarkerX`/`MarkerY` are
		// NULLABLE, so the guard's `goal.MapId == 0` was false for a null and 23 markerless goals were typed ReachArea
		// - a silent break the row counts above would never have shown.
		[Fact]
		public async Task EveryReachAreaGoal_HasAMarkerToReach()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var goals = await db
				.QuestGoalDefinitions.AsNoTracking()
				.Where(g => g.GoalType == QuestGoalType.ReachArea)
				.Select(g => new { g.QuestDefinitionId, g.GoalId, g.MapId, g.MarkerX, g.MarkerY, g.RegionId })
				.ToListAsync(ct);

			Assert.NotEmpty(goals);

			// A RegionId is the other way to name a place (the client's own bounded-area volume), so either will do.
			var placeless = goals
				.Where(g => g.RegionId == null && (g.MapId is null or 0 || (g.MarkerX == null && g.MarkerY == null)))
				.Select(g => $"quest {g.QuestDefinitionId} goal {g.GoalId}")
				.ToArray();

			Assert.Equal([], placeless);
		}

		[Fact]
		public async Task EveryEssentialTable_CarriesItsContent()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var counts = new (string Table, int Actual, int Floor)[]
			{
				("Items", await db.Items.CountAsync(ct), 1000),
				("Spells", await db.Spells.CountAsync(ct), 100),
				("Maps", await db.Maps.CountAsync(ct), 10),
				("NpcTemplates", await db.NpcTemplates.CountAsync(ct), 100),
				("NpcSpawns", await db.NpcSpawns.CountAsync(ct), 500),
				("NpcSpawnEquipment", await db.NpcSpawnEquipment.CountAsync(ct), 1000),
				("NpcSpawnMorphs", await db.NpcSpawnMorphs.CountAsync(ct), 1000),
				("NpcSpawnAreas", await db.NpcSpawnAreas.CountAsync(ct), 500),
				("WorldObjects", await db.WorldObjects.CountAsync(ct), 500),
				("Shops", await db.Shops.CountAsync(ct), 10),
				("ShopItems", await db.ShopItems.CountAsync(ct), 300),
				("QuestDefinitions", await db.QuestDefinitions.CountAsync(ct), 50),
				("ConversationNodes", await db.ConversationNodes.CountAsync(ct), 100),
				("ConversationEdges", await db.ConversationEdges.CountAsync(ct), 100),
				("ConversationEntries", await db.ConversationEntries.CountAsync(ct), 10),
				("ConversationPackets", await db.ConversationPackets.CountAsync(ct), 300),
				("ConversationScreenParameters", await db.ConversationScreenParameters.CountAsync(ct), 3000),
				("Recipes", await db.Recipes.CountAsync(ct), 100),
				("RecipeItems", await db.RecipeItems.CountAsync(ct), 1000),
				("AppearanceChoices", await db.AppearanceChoices.CountAsync(ct), 300),
				("PatrolNodes (roots)", await db.PatrolNodes.CountAsync(n => n.ParentPath == null, ct), 50),
				("PatrolNodes", await db.PatrolNodes.CountAsync(ct), 300)
			};

			var empty = counts.Where(c => c.Actual < c.Floor).Select(c => $"{c.Table}={c.Actual} (floor {c.Floor})").ToList();
			Assert.True(empty.Count == 0, $"The shipped seed is missing content: {string.Join(", ", empty)}");
		}

		// Every race's Totem of Origins destination actually resolves. RecallToRacialHomeCommand maps the character's
		// Race to a home city map and then takes that map's default respawn point, so a map id that is absent from the
		// seed - or present with no respawn point - means the Totem silently does nothing for everyone of that race.
		// Both failures are content gaps a row count cannot see, and the map ids are hand-authored constants rather
		// than imported rows, which is exactly the kind of thing that rots when the seed is re-harvested.
		[Fact]
		public async Task EveryRacialHomeMap_HasADefaultSpawn()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;
			var broken = new List<string>();

			foreach (var (race, mapId) in StartingData.RacialHomeMap)
			{
				if (!await db.Maps.AnyAsync(m => m.Id == mapId, ct))
				{
					broken.Add($"{race} -> map {mapId} is not in the seed");
					continue;
				}

				// Mirrors GetDefaultSpawn: the AlwaysKnown starter, else any point on the map.
				var points = db.RespawnPoints.Where(r => r.MapId == mapId);
				if (!await points.AnyAsync(ct))
					broken.Add($"{race} -> map {mapId} has no respawn point at all");
				else if (!await points.AnyAsync(r => r.AlwaysKnown, ct))
					broken.Add($"{race} -> map {mapId} has no AlwaysKnown point, so arrival depends on row order");
			}

			Assert.True(broken.Count == 0, $"Totem of Origins destinations that do not resolve: {string.Join("; ", broken)}");
		}

		// The OUTLAW resurrection points survived the harvest. RespawnPoint.Shady comes from a single RDB attribute that
		// 222 of the exported elements omit entirely, so a parser or dialect regression does not fail loudly - it just
		// silently files every criminal point as a normal one, and the whole outlaw revive destination quietly becomes
		// "wherever a law-abiding player would go". A bare RespawnPoints row count cannot see that, which is precisely
		// why this asserts on the FLAG rather than the table.
		[Fact]
		public async Task OutlawRespawnPoints_AreInTheSeed()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var shady = await db.RespawnPoints.Where(r => r.Shady).ToListAsync(ct);
			Assert.True(shady.Count >= 40, $"The seed carries {shady.Count} outlaw (shady) respawn points; the import yields 49 across the major playfields.");

			// One per playfield, and known without discovery - an outlaw is sent there, they do not go looking for it.
			Assert.All(shady, p => Assert.True(p.AlwaysKnown, $"Outlaw point {p.Id} on map {p.MapId} is not AlwaysKnown."));
			Assert.Equal(shady.Count, shady.Select(p => p.MapId).Distinct().Count());

			// The flag must agree with the designers' labels: no point NAMED for the outlaw system may be filed normal.
			// That is the regression this catches - the naming and the attribute agree exactly in the client's export.
			var misfiled = await db
				.RespawnPoints.Where(r => !r.Shady && (r.Name.Contains("Outlaw") || r.Name.Contains("Shady")))
				.Select(r => $"{r.Id} '{r.Name}'")
				.ToListAsync(ct);
			Assert.True(misfiled.Count == 0, $"Outlaw-named points filed as normal: {string.Join(", ", misfiled)}");
		}

		// The imported bounded-area regions and, more importantly, their LINKS (#519/#521). A MapRegion nothing points
		// at is dead weight: the whole point of the table is that only regions a consumer resolves to are imported, so
		// an unlinked row means the resolution silently stopped working while the row count looked healthy.
		[Fact]
		public async Task MapRegions_AreLinkedToTheirConsumers()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var regions = await db.MapRegions.Include(r => r.Vertices).ToListAsync(ct);
			Assert.NotEmpty(regions);

			// Every region is a testable prism: a real ring and a real height range. A degenerate one can never
			// contain anybody, so it would disable the consumer it was imported for without failing anything.
			Assert.All(regions, r =>
			{
				Assert.True(r.Vertices.Count >= 3, $"region {r.Name} on map {r.MapId} has {r.Vertices.Count} vertices");
				Assert.True(r.MaxY > r.MinY, $"region {r.Name} on map {r.MapId} has an empty height range");
				Assert.Equal(Enumerable.Range(0, r.Vertices.Count), r.Vertices.OrderBy(v => v.Sequence).Select(v => v.Sequence));
			});

			var discovery = await db.RespawnPoints.CountAsync(p => p.DiscoveryRegionId != null, ct);
			var goals = await db.QuestGoalDefinitions.CountAsync(g => g.RegionId != null, ct);
			var banners = await db.MapRegions.CountAsync(r => r.AreaNameId != null, ct);
			var hazards = await db.MapHazards.CountAsync(ct);
			var noMount = await db.MapRegions.CountAsync(r => r.NoMount, ct);
			Assert.True(discovery >= 68, $"{discovery} respawn points carry a discovery volume; the import resolves 68 (#519/#682).");
			Assert.True(goals >= 3, $"{goals} quest goals carry a region; the import resolves 3.");
			Assert.True(banners >= 140, $"{banners} volumes carry an area banner; the import resolves 140+ (#513/#679/#680).");
			Assert.True(hazards >= 100, $"{hazards} hazard volumes carry behaviour; the lava import resolves 100+ (#504).");
			Assert.True(noMount == 14, $"{noMount} volumes forbid riding; the client ships 14 (#681).");

			var linked = (await db.RespawnPoints.Where(p => p.DiscoveryRegionId != null).Select(p => p.DiscoveryRegionId!.Value).ToListAsync(ct))
				.Concat(await db.QuestGoalDefinitions.Where(g => g.RegionId != null).Select(g => g.RegionId!.Value).ToListAsync(ct))
				.Concat(await db.MapRegions.Where(r => r.AreaNameId != null).Select(r => r.Id).ToListAsync(ct))
				.Concat(await db.MapHazards.Select(h => h.RegionId).ToListAsync(ct))
				// A no-mount volume is consumed by its own flag - there is no other table to point from, because the
				// behaviour has no parameters to put in one.
				.Concat(await db.MapRegions.Where(r => r.NoMount).Select(r => r.Id).ToListAsync(ct))
				.ToHashSet();
			var orphans = regions.Where(r => !linked.Contains(r.Id)).Select(r => $"{r.MapId}/{r.Name}").ToList();
			Assert.True(orphans.Count == 0, $"MapRegions nothing links to: {string.Join(", ", orphans)}");
		}

		// The #519 claim, asserted on the shipped data rather than in prose: a discovery volume is TIGHTER than the
		// 30-unit radius it replaces. If a future harvest widened them, the "our radius is too generous" premise the
		// column exists for would be false and nobody would notice.
		[Fact]
		public async Task DiscoveryVolumes_AreTighterThanTheFallbackRadius()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var volumes = await db
				.RespawnPoints.Where(p => p.DiscoveryRegionId != null)
				.Select(p => p.DiscoveryRegion!)
				.Distinct()
				.ToListAsync(ct);

			Assert.NotEmpty(volumes);
			Assert.All(volumes, r =>
			{
				// The fallback is a 30-unit radius: a 60 x 60 footprint, unbounded vertically.
				Assert.True(r.MaxX - r.MinX < 60f && r.MaxZ - r.MinZ < 60f,
					$"{r.Name} is {r.MaxX - r.MinX:F0} x {r.MaxZ - r.MinZ:F0}, no tighter than the 60 x 60 radius box it replaces");
				Assert.True(r.MaxY - r.MinY < 60f, $"{r.Name} is {r.MaxY - r.MinY:F0} units tall - the volumes are ~10");
			});
		}

		// No GATHER objective is left at the fresh-harvest default (#521). A goal whose trailing reference is an
		// item (LDB category 50200) and whose kind tag says it acquires one is a collect objective; they shipped
		// un-typed, which is a quest that can never complete. `TypeItemGoalsFromTrailingCategory` types them, and this
		// stops a re-harvest reintroducing the inert form: the row count never moves, only the GoalType does.
		//
		// ⚠️ That default was `Kill` until #522, so "un-typed" and "kill the NPC whose template id equals this item id"
		// were the same row. This half of the gate reads `None` now for exactly that reason.
		//
		// ⚠️ The verbs where OBTAINING the item is not DOING the objective are EXCLUDED on purpose and must stay
		// excluded - `Deliver` hands it over, `Craft` makes it, `Use` uses it on something, `DestroyObject` destroys a
		// thing that never enters the bag, and `Investigate` is a place you go and look at. Typing any of them collect
		// completes the quest the moment the item reaches the backpack, skipping the objective: a silent chain bug,
		// where inert is at least visibly stuck. Both halves of this gate carry the same set, so a future "tidy up the
		// last inert goals" pass has to answer the verb rather than just widening the rule.
		//
		// ⚠️ It is ONE list on both sides for a reason. It used to exclude `Deliver` alone; when #522 widened the
		// importer's rule to all five, the twenty rows it correctly reset to un-wired all failed the first half here -
		// the gate asserting the very mistyping the same commit had just removed.
		private static readonly uint[] NotSatisfiedByArrival =
		[
			(uint)QuestGoalKind.Deliver, (uint)QuestGoalKind.Craft, (uint)QuestGoalKind.Use, (uint)QuestGoalKind.DestroyObject, (uint)QuestGoalKind.Investigate
		];

		[Fact]
		public async Task ItemGoals_AreTypedByTheirKindTag()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var inert = await db
				.QuestGoalDefinitions.Where(g => g.TrailingCategory == 50200 && g.TrailingRefId > 0
				                                 && g.GoalType == QuestGoalType.None
				                                 && !NotSatisfiedByArrival.Contains(g.Separator ?? 0))
				.Select(g => $"goal {g.GoalId} (quest {g.QuestDefinitionId}, kind {g.Separator})")
				.ToListAsync(ct);

			Assert.True(inert.Count == 0, $"acquire-kind item goals still un-typed: {string.Join(", ", inert)}");

			// ...and the typing actually happened rather than the category vanishing from the harvest. 42, not the 62
			// this asserted before #522: nineteen of that 62 were the mistyped rows above, and the twentieth is
			// "Investigate the Ancient Gateway", which is a ReachArea rather than a collect.
			var collect = await db.QuestGoalDefinitions.CountAsync(g => g.TrailingCategory == 50200 && g.GoalType == QuestGoalType.CollectItem, ct);
			Assert.True(collect >= 42, $"only {collect} item goals are CollectItem; the harvest carries 42.");

			// ...and none of the five is quietly collect-typed.
			var mistyped = await db
				.QuestGoalDefinitions.Where(g => NotSatisfiedByArrival.Contains(g.Separator ?? 0) && g.GoalType == QuestGoalType.CollectItem)
				.Select(g => $"goal {g.GoalId} (kind {g.Separator})")
				.ToListAsync(ct);

			Assert.True(mistyped.Count == 0, $"goals whose verb is not satisfied by the item arriving are typed CollectItem: {string.Join(", ", mistyped)}");
		}

		// A goal is typed `Kill` because the CLIENT called it a kill, and for no other reason (#522).
		//
		// This is the gate on `QuestGoalType.Kill` no longer being enum value 0. It was, so it was also the un-typed
		// default: 7,148 catalog goals with no verb and no target claimed to be kill goals, and every guard in
		// `QuestProgress.MatchingKillGoal` is a patch over one way that misfired - the PARTIAL exclusion, then the
		// zero-template-id guard. Reverting the default (or adding a rule that types a goal Kill for any reason but its
		// verb) puts that whole class of false-fire back, silently, so it is worth a gate rather than a comment.
		//
		// The escape hatch is deliberate and narrow: a goal with a `TargetName` was configured BY HAND (a Seed* or the
		// Quest editor), and a human naming the NPC to kill is evidence in its own right - it is how the beach and
		// Casilda chains are wired. A goal with neither the verb nor a hand-set target has no evidence at all.
		[Fact]
		public async Task KillGoals_AreTypedFromTheClientsOwnVerb()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var unevidenced = await db
				.QuestGoalDefinitions.Where(g => g.GoalType == QuestGoalType.Kill && g.Separator != (uint)QuestGoalKind.Kill && g.TargetName == null)
				.Select(g => $"goal {g.GoalId} (quest {g.QuestDefinitionId}, kind {g.Separator})")
				.ToListAsync(ct);

			Assert.True(unevidenced.Count == 0,
				$"goals typed Kill with neither the client's kill verb nor a hand-set target: {string.Join(", ", unevidenced)}");

			// ...and the typing ran, rather than the verb vanishing from the harvest. All 30 kill-verb goals are typed:
			// the 24 naming an NPC template (51000) and the 6 naming a monster CATEGORY (51100), which are typed and
			// inert because the group resolves to no NPC we hold.
			var byVerb = await db.QuestGoalDefinitions.CountAsync(g => g.Separator == (uint)QuestGoalKind.Kill && g.GoalType == QuestGoalType.Kill, ct);
			Assert.True(byVerb >= 30, $"only {byVerb} kill-verb goals are typed Kill; the harvest carries 30.");

			// The other half of the same rule, on the runtime side: MatchingKillGoal only matches TrailingRefId against a
			// killed entity's template id when the goal's own TYPED reference says it holds one. Nothing may be typed
			// Kill while pointing at an item or a bounded area - those ids are different spaces, and comparing them is
			// safe only until one collides.
			var wrongSpace = await db
				.QuestGoalDefinitions.Where(g => g.GoalType == QuestGoalType.Kill && g.TrailingRefId > 0
				                                 && g.TrailingCategory != 0 && g.TrailingCategory != QuestProgress.NpcTemplateNameCategory
				                                 && g.TrailingCategory != 51100)
				.Select(g => $"goal {g.GoalId} (category {g.TrailingCategory})")
				.ToListAsync(ct);

			Assert.True(wrongSpace.Count == 0, $"Kill goals whose target reference is not an NPC: {string.Join(", ", wrongSpace)}");
		}

		// A goal's own SENTENCE outranks its map marker (#522).
		//
		// The marker is the weakest evidence a goal carries, and `TypeLocationGoalsFromTrailingCategory` was reading it
		// first: a location reference plus a pin became `ReachArea`. But *"Speak to Chulainn"* has a pin at Chulainn's
		// feet and *"Escort Casilda to the city"* has one where Casilda is STANDING when the escort is granted, so a
		// reach completes both by walking up to the NPC - seven talk goals without their conversation ever playing, and
		// the escort the instant it began.
		//
		// It was invisible because those rows had been wired by a favourable ORDERING in some earlier run and the
		// bootstrap guard then hid the rule; only rebuilding the wiring from scratch showed it. So this gate reads the
		// shipped rows rather than the rule - it is the state a clean rebuild has to keep reproducing.
		[Fact]
		public async Task GoalsWhoseObjectiveIsATalkOrEscort_AreNotTypedFromTheirMarker()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var reaches = await db
				.QuestGoalDefinitions.Where(g => g.GoalType == QuestGoalType.ReachArea && g.Name != null)
				.Select(g => new { g.GoalId, Name = g.Name! })
				.ToListAsync(ct);

			Assert.NotEmpty(reaches);

			// Two deliberate exceptions, both hand-seeded with their reason in the seed (SeedShipwreckedCityChain):
			// 1352 "Talk to Nadini" - Nadini has no imported conversation, so a greeting cannot fire; her spawn is
			// exactly on the marker. 3492 "Unlock the Acheronian gate" - the gate object stands on the marker.
			var seededExceptions = new uint[] { 1352 };

			var fromMarker = reaches
				.Where(g => !seededExceptions.Contains(g.GoalId) && QuestObjectiveText.NeedsMoreThanReachingItsMarker(g.Name))
				.Select(g => $"goal {g.GoalId} '{g.Name}'")
				.ToList();

			Assert.True(fromMarker.Count == 0,
				$"goals typed ReachArea whose own objective asks for a talk or an escort: {string.Join(", ", fromMarker)}");
		}

		// The kind tag agrees with the wiring wherever a human established the goal's semantics from a capture. This is
		// what the QuestGoalKind decode rests on, so it is worth failing loudly if a re-harvest disturbs it - most of
		// all the pair that proves the tag describes the VERB and not the target: quest 12371's two goals reference the
		// same item with the same count, and are 23 (collect) and 20 (deliver).
		[Fact]
		public async Task GoalKindTag_AgreesWithTheHandWiredGoals()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;
			var goals = await db.QuestGoalDefinitions.Where(g => g.GoalId == 1448 || g.GoalId == 3490 || g.QuestDefinitionId == 12371).ToListAsync(ct);

			var collect = goals.Single(g => g.GoalId == 3495);
			var deliver = goals.Single(g => g.GoalId == 3497);
			Assert.Equal(collect.TrailingRefId, deliver.TrailingRefId); // the SAME item...
			Assert.Equal(collect.RequiredCount, deliver.RequiredCount); // ...and the same count...
			Assert.Equal((uint)QuestGoalKind.Collect, collect.Separator); // ...but different kinds
			Assert.Equal((uint)QuestGoalKind.Deliver, deliver.Separator);

			Assert.Equal((uint)QuestGoalKind.RegionScoped, goals.Single(g => g.GoalId == 1448).Separator);
			Assert.Equal((uint)QuestGoalKind.Kill, goals.Single(g => g.GoalId == 3490).Separator);
		}

		// Every goal whose trailing reference is an npc_template (LDB category 51000) names its target, and where we
		// hold that template the name is the CATALOG's - i.e. the exact string the runtime compares against
		// (QuestProgress.MatchingKillGoal's name fallback, ComputeGiverMarkerState).
		//
		// The distinction is the whole point of #521's work item 1. LDB[51000] is keyed by template id and carries the
		// client's own name, but 27 of our 1,018 templates were captured under the shared placeholder `Halen van
		// Dummy`, and for those the LDB's real name would look resolved and never match. So the catalog wins wherever
		// it exists; the LDB fills only the templates we do not hold, which are inert until their map is imported.
		[Fact]
		public async Task NpcGoals_NameTheirTargetWithTheCatalogsOwnName()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var goals = await db.QuestGoalDefinitions.Where(g => g.TrailingCategory == 51000 && g.TrailingRefId > 0).ToListAsync(ct);
			Assert.True(goals.Count >= 24, $"only {goals.Count} npc-referencing goals - the harvest carries 24.");

			var unnamed = goals.Where(g => string.IsNullOrEmpty(g.TargetName)).Select(g => g.GoalId).ToList();
			Assert.True(unnamed.Count == 0, $"npc-referencing goals with no TargetName: {string.Join(", ", unnamed)}");

			var templates = await db.NpcTemplates.ToDictionaryAsync(t => t.TemplateId, t => t.Name, ct);
			foreach (var g in goals)
			{
				if (templates.TryGetValue(g.TrailingRefId, out var stored))
					Assert.Equal(stored, g.TargetName);
			}
		}

		// A quest-gated kill target must be killable ENOUGH TIMES to finish its goal. The gate forces NoRespawn (a
		// story target stays dead), so the world has to hold at least `RequiredCount` spawns of that name - otherwise
		// the goal sticks one short with nothing left alive and the quest is permanently uncompletable.
		//
		// ⚠️ This is the gate on the bug #521 surfaced, not a hypothetical. Membership used to be DERIVED from "a Kill
		// goal that has a TargetName", which was true of the only two goals ever wired (Saddur, Scavenger Tuut - both
		// unique) and false the moment the target names were filled mechanically: quest 12057 asks for 9 Vanir Polluter
		// kills against 8 spawns in the world. `GatesTargetVisibility` is now an explicit, hand-set column, and this
		// asserts the property that makes setting one safe rather than the current membership.
		[Fact]
		public async Task GatedKillTargets_HaveEnoughSpawnsToFinishTheGoal()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var gated = await db.QuestGoalDefinitions.Where(g => g.GatesTargetVisibility).ToListAsync(ct);
			Assert.NotEmpty(gated); // the two story targets; an empty set would mean the flag was lost, not that it is unused

			var spawnsByName = await db
				.NpcSpawns.GroupBy(s => s.NpcTemplate.Name)
				.Select(grp => new { Name = grp.Key, Count = grp.Count() })
				.ToDictionaryAsync(x => x.Name, x => x.Count, ct);

			foreach (var g in gated)
			{
				Assert.False(string.IsNullOrEmpty(g.TargetName), $"goal {g.GoalId} gates visibility but names no target");
				var spawns = spawnsByName.GetValueOrDefault(g.TargetName!, 0);
				var needed = Math.Max(1, (int)g.RequiredCount);
				Assert.True(spawns >= needed,
					$"goal {g.GoalId} (quest {g.QuestDefinitionId}) needs {needed} kill(s) of '{g.TargetName}' but the world holds {spawns} spawn(s), " +
					"and a gated target does not respawn - the quest can never complete.");
			}
		}

		// A TRIGGER-SPAWNED type is never a world placement (#528/#501). Two kinds today: a `NpcAmbush` ambusher (a
		// quest goal advancing spawns it) and a `NpcEncounter` BOSS (a proximity lure swaps into it). Neither stands
		// anywhere, so a `NpcSpawns` row for one is a harvest of the trigger FIRING, and rendering it puts a creature
		// in the world permanently that retail only ever shows to a player who triggered it.
		//
		// ⚠️ This is a gate on something that used to be an ACCIDENT. The Bat Demon really did stand in the Tortage
		// quarry for every player; the encounter boss escaped only because she spawns on the exact spot her lure
		// vacated and the position dedup dropped her (#501) - which would have stopped protecting her the moment a
		// capture caught the swap a few units off. `ImportOps.BuildBurstTruthSpawns` now excludes both by rule.
		[Fact]
		public async Task NoTriggerSpawnedType_HasAWorldPlacement()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var ambushers = await db.NpcAmbushes.Select(a => a.NpcTemplateId).ToListAsync(ct);
			var bosses = await db.NpcEncounters.Select(e => e.BossNpcTemplateId).ToListAsync(ct);
			var triggerSpawned = ambushers.Concat(bosses).ToHashSet();
			Assert.NotEmpty(triggerSpawned); // an empty set would mean the tables were lost, not that the rule is unused

			var placed = await db
				.NpcSpawns.Where(s => triggerSpawned.Contains(s.NpcTemplateId))
				.Select(s => new { s.NpcTemplateId, s.NpcTemplate.Name, s.MapId })
				.ToListAsync(ct);

			Assert.True(placed.Count == 0,
				"trigger-spawned types with a world placement: " +
				string.Join(", ", placed.Select(p => $"{p.Name} ({p.NpcTemplateId}) on map {p.MapId}")));
		}

		// ⚠️ NO SHIPPED GOAL CARRIES KIND 0, because the wire never sends one (#548). `QuestGoalKind` used to declare
		// `Untargeted = 0` on the strength of 11 rows holding it - which were not a kind at all but the importer's
		// un-harvested default: the full-update path set `Separator` nowhere, so every goal the corpus only ever
		// full-updates read as 0. The tag takes 5/9/10/12/20/22/23/24/25 across all 158 snapshot goals AND all 111
		// full-update ones, and 0 is not among them.
		//
		// This is the gate on that, and it is a REGRESSION gate rather than a decode claim: a re-harvest that stops
		// lifting the tag would silently repopulate the phantom, and every one of those goals would go back to
		// reading as an untargeted verb-less objective.
		[Fact]
		public async Task NoGoalCarriesTheUnharvestedKindZero()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var untagged = await db
				.QuestGoalDefinitions.Where(g => g.Separator == 0)
				.Select(g => $"goal {g.GoalId} (quest {g.QuestDefinitionId})")
				.ToListAsync(ct);

			Assert.True(untagged.Count == 0, "goals with no kind tag - the full-update's `K` word is not being harvested:\n  " + string.Join("\n  ", untagged));

			// ...and the tag is only ever a kind the corpus actually shows, so a stray value is caught too. NULL is
			// excluded rather than asserted against: it means "never seen on the wire", which is every goal that comes
			// from the client's quest catalog rather than a capture (see QuestGoalDefinition.Separator).
			var known = Enum.GetValues<QuestGoalKind>().Select(k => (uint)k).ToHashSet();
			var stray = await db.QuestGoalDefinitions.Where(g => g.Separator != null).Select(g => g.Separator!.Value).Distinct().ToListAsync(ct);
			Assert.All(stray, s => Assert.Contains(s, known));
		}

		// The quest INDEX's per-line catalog may never contain a quest the captures never showed.
		//
		// This is the regression the client's quest catalog (RDB 1000104) introduced and the goldens did NOT catch.
		// The index block lists its line's quest ids plus a (sequence, remaining) pair built from the line's COUNT, so
		// the 3011 catalog-only quests silently changed that region for the 56 snapshot-eligible quests that share a
		// line with one. The only index golden covers quest 10238, whose line 241 happened to be complete already -
		// 8 members before and after - so the suite stayed green while a live login would have shipped a region no
		// capture backs.
		//
		// It goes through the REPOSITORY on purpose. The filter that fixes this lives in GetQuestDefinitionsByLine,
		// so a test that re-implements the query would pass no matter what the repository does - which is exactly how
		// this got through the first time.
		[Fact]
		public async Task QuestIndexCatalog_HoldsOnlyQuestsTheCapturesShowed()
		{
			var options = TestDb.ReadOnly(TestDb.GameDatabase);
			var repo = new DatabaseRepository(new SeedContextFactory(options));
			await using var db = new DatabaseContext(options);
			var ct = TestContext.Current.CancellationToken;

			// The lines that can actually reach a login snapshot - the ones whose index region is built and sent.
			var lines = await db
				.QuestDefinitions.Where(q => q.HasSnapshotRecord)
				.Select(q => q.QuestLineId)
				.Distinct()
				.ToListAsync(ct);

			Assert.NotEmpty(lines);

			var withRefs = (await db.QuestTextRefs.Select(r => r.QuestId).Distinct().ToListAsync(ct)).ToHashSet();

			var leaked = new List<string>();
			foreach (var line in lines)
			foreach (var q in await repo.GetQuestDefinitionsByLine(line))
				if (!withRefs.Contains(q.QuestId))
					leaked.Add($"quest {q.QuestId} (line {line}, '{q.Name}')");

			Assert.True(leaked.Count == 0,
				$"{leaked.Count} catalog-only quest(s) reached a snapshot-eligible line's index catalog. They carry no journal " +
				"text, so no capture ever listed them - shipping them changes the index block's id list AND its `remaining` " +
				"count:\n  " + string.Join("\n  ", leaked.Take(20)));
		}

		// The two quests that carry a CAPTURED line catalog (IndexQuestLineQuestIds) are a byte-level oracle for the
		// list the index builder derives from the DB: the CSV is what the wire actually sent.
		//
		// ⚠️ Necessary but NOT sufficient, and worth knowing why: both of them sit in lines the catalog did not grow
		// (241 and 1054), so this passes even with the bug above. It locks the derivation for the two lines we can
		// prove; QuestIndexCatalog_HoldsOnlyQuestsTheCapturesShowed is what covers the other 80.
		[Fact]
		public async Task DerivedLineCatalog_MatchesTheCapturedCsv()
		{
			var options = TestDb.ReadOnly(TestDb.GameDatabase);
			var repo = new DatabaseRepository(new SeedContextFactory(options));
			await using var db = new DatabaseContext(options);
			var ct = TestContext.Current.CancellationToken;

			var withCsv = await db
				.QuestDefinitions.Where(q => q.IndexQuestLineQuestIds != null)
				.Select(q => new { q.QuestId, q.QuestLineId, q.IndexQuestLineQuestIds })
				.ToListAsync(ct);

			Assert.NotEmpty(withCsv);

			foreach (var q in withCsv)
			{
				var derived = (await repo.GetQuestDefinitionsByLine(q.QuestLineId)).Select(d => d.QuestId).ToList();
				var captured = q.IndexQuestLineQuestIds!.Split(',').Select(uint.Parse).ToList();
				Assert.Equal(captured, derived);
			}
		}

		private sealed class SeedContextFactory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
		{
			public DatabaseContext CreateDbContext()
			{
				return new DatabaseContext(options);
			}

			public Task<DatabaseContext> CreateDbContextAsync(CancellationToken ct = default)
			{
				return Task.FromResult(new DatabaseContext(options));
			}
		}

		// Every server-opened conversation is REACHABLE: it has a trigger the runtime can fire, and an NPC to speak it.
		//
		// The two kinds are complementary and exhaustive - a world object (use it) or an approach (walk up to the NPC) -
		// so a node with neither is dead data: imported, keyed, and unreachable by any code path. That is precisely the
		// state #523 existed to fix, so it is worth failing on rather than rediscovering.
		[Fact]
		public async Task ServerOpenedConversations_AreReachable()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var openings = await db
				.Set<ConversationEntry>().Where(e => e.Trigger != ConversationTrigger.Talk)
				.Select(e => new { e.Id, e.NpcName, e.Trigger, e.TriggerWorldObjectId })
				.ToListAsync(ct);

			Assert.NotEmpty(openings); // Casilda's city-door farewell and Saddur's beach confrontation are shipped content

			foreach (var o in openings)
			{
				// The trigger is what keeps these OUT of the talk path: the runtime looks a root up BY trigger, so a
				// server-opened root can never be reached by clicking the NPC - which is not how retail opens it.
				Assert.Equal(o.Trigger == ConversationTrigger.UseWorldObject, o.TriggerWorldObjectId != null);

				// An approach trigger needs no column - the NPC is the target - but it does need an NPC that exists.
				Assert.True(await db.Set<NpcTemplate>().AnyAsync(t => t.Name == o.NpcName, ct),
					$"server-opened conversation for '{o.NpcName}' has no NpcTemplate - nothing can speak it.");

				if (o.TriggerWorldObjectId is { } obj)
					Assert.True(await db.Set<WorldObject>().AnyAsync(w => w.Id == obj, ct),
						$"'{o.NpcName}' is triggered by world object {obj}, which is not in WorldObjects.");
			}
		}

		// The escort can actually END. Its completion is data-driven off the escortee's SERVER-OPENED conversation
		// (#521/#523): using the world object that plays her farewell is what completes the goal, so the chain closes
		// only if the shipped data still connects escort goal -> escortee -> a server-initiated node with a trigger.
		//
		// Worth a gate because the link spans three tables and a re-harvest could quietly break any leg of it - and the
		// symptom would be an escort that silently never ends, which is exactly the state this replaced.
		[Fact]
		public async Task EscortGoal_HasAWorldObjectThatCanCompleteIt()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var escorts = await db
				.QuestGoalDefinitions.Where(g => g.GoalType == QuestGoalType.Escort && g.TargetName != null)
				.Select(g => new { g.GoalId, Target = g.TargetName! })
				.ToListAsync(ct);

			Assert.NotEmpty(escorts); // the Tortage escort (12407/3544, Casilda) is shipped content

			foreach (var escort in escorts)
			{
				var trigger = await db
					.Set<ConversationEntry>()
					.Where(e => e.NpcName == escort.Target && e.Trigger == ConversationTrigger.UseWorldObject && e.TriggerWorldObjectId != null)
					.Select(e => e.TriggerWorldObjectId)
					.FirstOrDefaultAsync(ct);

				Assert.True(trigger != null,
					$"escort goal {escort.GoalId} targets '{escort.Target}', who has no server-opened conversation with a world-object trigger - nothing can complete the escort.");

				// ...and that object must still exist, or the trigger points at nothing.
				Assert.True(await db.Set<WorldObject>().AnyAsync(w => w.Id == trigger, ct),
					$"escort goal {escort.GoalId}'s trigger object {trigger} is not in WorldObjects.");
			}
		}

		// A `Deliver` goal hands its item over by TALKING to the recipient, so the trigger must be TalkToNpc - never
		// CollectItem, which would complete the quest the moment the player picks the item up and skip the delivery.
		//
		// ⚠️ It deliberately does NOT require the recipient to be an NPC we hold. The name comes from the client's own
		// objective text, the goal is inert either way, and recording it means the goal works the moment its map is
		// imported. Coverage over precision was an explicit call; what is NOT allowed is a wrong TRIGGER, which would
		// mis-complete a quest we can actually play.
		[Fact]
		public async Task DeliverGoals_HandInByTalking()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var wired = await db
				.QuestGoalDefinitions.Where(g => g.Separator == (uint)QuestGoalKind.Deliver && g.TargetName != null)
				.Select(g => new { g.GoalId, g.GoalType, Target = g.TargetName! })
				.ToListAsync(ct);

			Assert.NotEmpty(wired);

			var wrongTrigger = wired.Where(g => g.GoalType != QuestGoalType.TalkToNpc).Select(g => $"goal {g.GoalId} is {g.GoalType}").ToList();
			Assert.True(wrongTrigger.Count == 0, $"Deliver goals with a target but not TalkToNpc: {string.Join(", ", wrongTrigger)}");

			// The two whose recipients we DO hold are the playable ones, pinned by name so a re-harvest that dropped or
			// mis-parsed either is caught. `Amun` in particular must not have become `Amunarton` (an exact-match test).
			Assert.Equal("Turach", wired.Single(g => g.GoalId == 3497).Target);
			Assert.Equal("Croiss", wired.Single(g => g.GoalId == 5792).Target);
			Assert.Equal("Amun", wired.Single(g => g.GoalId == 4334).Target);
		}

		[Fact]
		public async Task EveryShop_HasWares()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var empty = await db.Shops.Where(s => !s.Items.Any()).Select(s => s.VendorName).ToListAsync(TestContext.Current.CancellationToken);

			Assert.True(empty.Count == 0, $"Shops with no wares: {string.Join(", ", empty)}");
		}

		// Same shape one level down: a conversation node with no packets serves nothing, and a screen with no
		// parameters renders an empty line.
		[Fact]
		public async Task EveryConversationNode_HasPackets()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var empty = await db.ConversationNodes.Where(n => !n.Packets.Any()).CountAsync(TestContext.Current.CancellationToken);

			Assert.True(empty == 0, $"{empty} conversation node(s) carry no packets.");
		}

		// Every SHIPPED patrol rebuilds into a wire task. This reads the committed ROWS, which is the gap that let a
		// crash ship: PatrolNodeRoundTripTests converts a captured record in memory (parse -> ToNode -> ToTask) and
		// never touches a row, so an importer that failed to PERSIST a column looked perfectly healthy. All ten columns
		// #410 added were NULL in the seed - a hand-written deep copy in the import path had never been taught about
		// them - and `ToTask` dereferences PathTrailerWord on every mode-1 node, so entering any of the ten maps with a
		// Path patrol threw inside EnsureZoneNpcs and aborted world entry, leaving a half-built zone with no NPCs.
		//
		// ToTask throws loudly on a missing column by design (a patrol that cannot be rebuilt byte-exact must not be
		// silently emitted short), which makes simply calling it the whole assertion.
		// A shipped world object with NO display element never lifted, so WorldObjectCodec refuses to build it and it
		// silently never spawns - dead weight in the seed. One such row shipped (the map 11040 "Siege Camp", whose
		// `show_popup_on_use` had no column) and only turned up because a re-import collided with it on the derived key.
		// The grammar side of this is WorldObjectCorpusLiftTests; this is the seed side, and it also catches a row left
		// STALE by a lift fix, since lifting changes the derived id and the old row is not deleted.
		[Fact]
		public async Task EveryShippedWorldObject_IsLifted()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var unlifted = await db.WorldObjects.Where(o => o.DisplayKind == null)
				.Select(o => new { o.Id, o.MapId, o.Name }).AsNoTracking().ToListAsync(ct);

			Assert.True(unlifted.Count == 0,
				$"{unlifted.Count} shipped world object(s) have no display element, so they can never spawn: " +
				string.Join(" | ", unlifted.Take(5).Select(o => $"{o.Id} map {o.MapId} {o.Name}")));
		}

		[Fact]
		public async Task EveryShippedPatrol_RebuildsIntoAWireTask()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var nodes = await db.PatrolNodes.Include(n => n.Waypoints).Include(n => n.Words).AsNoTracking().ToListAsync(ct);
			Assert.NotEmpty(nodes);

			// Rebuild the parent/child links from the stored path keys - the same shape the runtime loads.
			var byKey = nodes.ToDictionary(n => (n.NpcSpawnId, n.OrdinalPath));
			foreach (var n in nodes.Where(n => n.ParentPath != null))
			{
				if (byKey.TryGetValue((n.NpcSpawnId, n.ParentPath!), out var parent))
					parent.Children.Add(n);
			}

			var failures = new List<string>();
			foreach (var root in nodes.Where(n => n.ParentPath == null))
			{
				try
				{
					PatrolNodeMapper.ToTask(root);
				}
				catch (Exception ex)
				{
					failures.Add($"spawn {root.NpcSpawnId}: {ex.Message}");
				}
			}

			Assert.True(failures.Count == 0, $"{failures.Count} shipped patrol(s) cannot be rebuilt: {string.Join(" | ", failures.Take(5))}");
		}

		// #703: every shipped route must be ONE CONTINUOUS polyline. The runtime walks a route waypoint by waypoint, so
		// a break in it is not a gap the NPC skips - it is walked, as a straight line between the two sides, through
		// whatever stands there. That is precisely how Old Tarantia's guards ended up crossing the buildings.
		//
		// A captured task carries the NPC's current short step as well as the beat it is walking, as two mode-1 legs of
		// one mode-4 list. Where they join (median 0.7u) they are genuinely one path; where they don't, concatenating
		// them fabricates a chord of up to 495.9u. PatrolPathExtractor.TrimToContinuousRoute drops the non-joining leg
		// at harvest; this is the seed side of that, and it is the assertion that would have caught the original bug in
		// the shipped data rather than in the client.
		[Fact]
		public async Task EveryShippedPatrol_IsOneContinuousPolyline()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var legs = await db.PatrolNodes.Include(n => n.Waypoints).AsNoTracking()
				.Where(n => n.Waypoints.Count > 0).ToListAsync(ct);
			Assert.NotEmpty(legs);

			var breaks = new List<string>();
			foreach (var spawn in legs.GroupBy(n => n.NpcSpawnId))
			{
				// Walk order is the tree's own ordering, the same one PatrolRoute.Waypoints concatenates in.
				var ordered = spawn.OrderBy(n => n.Ordinal).ThenBy(n => n.OrdinalPath, StringComparer.Ordinal).ToList();
				for (var i = 1; i < ordered.Count; i++)
				{
					var previous = ordered[i - 1].Waypoints.OrderBy(w => w.Ordinal).Last();
					var next = ordered[i].Waypoints.OrderBy(w => w.Ordinal).First();
					var gap = Vector3.Distance(
						new Vector3(previous.PositionX, previous.PositionY, previous.PositionZ),
						new Vector3(next.PositionX, next.PositionY, next.PositionZ));

					if (gap > PatrolLegJoinTolerance)
						breaks.Add($"spawn {ordered[i].NpcSpawnId}: {gap:F1}u between legs '{ordered[i - 1].OrdinalPath}' and '{ordered[i].OrdinalPath}'");
				}
			}

			Assert.True(breaks.Count == 0,
				$"{breaks.Count} shipped route(s) are not continuous - each break is walked as a straight line through whatever is between: {string.Join(" | ", breaks.Take(5))}");
		}

		// A CLASS-SPECIFIC map (the city armories) holds a different NPC set per class, so every spawn on one must say
		// which class it belongs to. A row left class-agnostic there is the pre-#494 bug in miniature: it is served to
		// all twelve classes ON TOP of their own set, so an armory shows one class's quartermasters to everybody.
		//
		// Also gates the mode: two classes cannot share one zone copy of a playfield whose population differs by class,
		// so a class-specific map has to be Solo. Shared would hand the second entrant whichever class's copy the first
		// one caused to be built.
		[Fact]
		public async Task EveryClassSpecificMap_IsSoloAndFullyAttributed()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var maps = await db.Maps.Where(m => m.ClassSpecific).AsNoTracking().ToListAsync(ct);
			Assert.NotEmpty(maps);

			var notSolo = maps.Where(m => m.InstanceMode != MapInstanceMode.Solo).Select(m => $"{m.Id} {m.Name} ({m.InstanceMode})").ToList();
			Assert.True(notSolo.Count == 0, $"Class-specific map(s) not instanced Solo: {string.Join(", ", notSolo)}");

			var mapIds = maps.Select(m => m.Id).ToList();
			var unattributed = await db
				.NpcSpawns.Where(n => mapIds.Contains(n.MapId) && n.ClassId == null)
				.Select(n => new
				{
					n.Id,
					n.MapId
				})
				.AsNoTracking()
				.ToListAsync(ct);

			Assert.True(unattributed.Count == 0,
				$"{unattributed.Count} spawn(s) on a class-specific map carry no class, so every class sees them: " + string.Join(" | ", unattributed.Take(5).Select(n => $"{n.Id} map {n.MapId}")));
		}

		// The Conarch Armory is the capture-proven case, and the shape is specific: the SAME five quartermaster posts
		// hold a different NPC per class (Zahur/Sabola, Akila/Imogen, Ebo/Chuma, Pancras/Ofir at identical positions),
		// which is exactly why the class had to join the spawn's unique index and its derived key. Without that, the
		// second class's import collided with the first and one whole set was silently dropped.
		[Fact]
		public async Task ConarchArmory_CarriesMoreThanOneClassSet()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var spawns = await db
				.NpcSpawns.Where(n => n.MapId == ConarchArmoryMapId)
				.Select(n => new
				{
					n.ClassId,
					n.NpcTemplateId,
					n.PositionX,
					n.PositionZ
				})
				.AsNoTracking()
				.ToListAsync(ct);

			var classes = spawns.Select(s => s.ClassId).Distinct().ToList();
			Assert.True(classes.Count >= 2, $"The Conarch Armory ({ConarchArmoryMapId}) carries only {classes.Count} class set(s) - the per-class population was lost.");

			// At least one post is genuinely SHARED between two classes with a different NPC on it. This is the
			// assertion a (map, position) key cannot survive.
			var contested = spawns.GroupBy(s => (s.PositionX, s.PositionZ)).Any(g => g.Select(s => s.NpcTemplateId).Distinct().Count() > 1);
			Assert.True(contested, "No armory post is held by a different NPC per class - the two class sets are no longer sharing positions, so this gate no longer proves anything.");
		}

		// #331: the ambient barks HARVESTED from the client's LDB are in the seed, and the two-hander rule held.
		//
		// A capture only catches the lines an NPC happened to say while someone was recording, so the importer fills in
		// the rest of each captured group's conversation from category 50015 - which it can do because that category is
		// plaintext and a line's id is computable from its token. The harvested rows are identifiable by their EMPTY
		// FormatKey: a key is only read for an encrypted string, so a harvested line legitimately has none.
		//
		// The second assertion is the one that matters. `RenownBystanderLovers` is one conversation shared by the
		// Nobleman and the Noblewoman, and its lines interleave the two speakers - so harvesting it would put her lines
		// in his mouth. If a keyless Lovers row ever appears, the single-speaker guard has been lost.
		[Fact]
		public async Task HarvestedBarks_AreInTheSeed_AndNoTwoHanderGroupWasHarvested()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var barks = await db.NpcChatter.AsNoTracking().ToListAsync(ct);
			var harvested = barks.Where(b => b.FormatKey.IsEmpty).ToList();

			Assert.True(harvested.Count >= 9, $"The seed carries {harvested.Count} harvested bark(s); the sweep yields 9 across the single-speaker groups.");
			Assert.True(barks.Count > harvested.Count, "Every bark is keyless - the CAPTURED lines have lost their keys.");

			// The two-hander must never be harvested onto either speaker.
			var lovers = harvested.Where(b => b.Group == "RenownBystanderLovers").ToList();
			Assert.True(lovers.Count == 0,
				$"{lovers.Count} line(s) of the shared Nobleman/Noblewoman conversation were harvested - the single-speaker guard is gone, so one speaker is now saying the other's lines.");

			// A harvested line must never carry a template slot: its wire form would need a <parameter> nobody fills.
			Assert.All(harvested, b => Assert.DoesNotContain("{", b.Group, StringComparison.Ordinal));
		}

		// #303: the abilities a LEVELLED character earns reach all twelve classes, not just the four the corpus has a
		// levelled character for.
		//
		// This is the gate on a CORPUS hole, which is why a row count cannot stand in for it. Every gameplay ability is
		// harvested from a per-character grant, and the corpus has exactly four levelled characters (Assassin 23,
		// HoX 24, Necro 80, Demo 80) - so without the universal pass, Search / Hide / Path of Asura land on those four
		// classes and a level-80 Barbarian has no search, no hide and no recall. That reads as a design decision in the
		// data and is really just "nobody captured a Barbarian past level 1".
		//
		// The other half matters as much: the pass must not over-generalise. Spellweaving, the pet bar and the mount
		// abilities are all held by exactly the two level-80 casters, and each is independently known NOT to be
		// universal - so if they ever appear on all twelve, the witness threshold has been weakened.
		[Fact]
		public async Task AbilitiesEveryLevelledClassEarns_ReachAllTwelveClasses()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var byName = await db
				.ClassAbilities.Join(db.Abilities, ca => ca.AbilityId, a => a.Id, (ca, a) => new
				{
					a.Name,
					ca.Class
				})
				.AsNoTracking()
				.ToListAsync(ct);
			var classCount = byName.GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.Select(x => x.Class).Distinct().Count());

			// Universal but NOT part of the level-1 create kit - the three the levelled roster agrees on.
			foreach (var ability in new[] { "Search", "Hide", "Path of Asura" })
				Assert.True(classCount.TryGetValue(ability, out var n) && n == 12,
					$"'{ability}' reaches {(classCount.TryGetValue(ability, out var m) ? m : 0)} of 12 classes - it is earned by every levelled class the corpus has, so withholding it from the rest is a capture artefact.");

			// Class kit, and it must stay class kit.
			foreach (var ability in new[] { "Spellweaving", "Pet Attack", "Pet Dismiss" })
				Assert.True(!classCount.TryGetValue(ability, out var n) || n < 12,
					$"'{ability}' reaches all 12 classes - it is held by exactly the two captured level-80 casters, so the universal-witness threshold has been weakened into treating a caster pair as the whole roster.");

			// A mount ability is not class kit at ALL, not even narrow class kit: it comes with the MOUNT (#303). It
			// used to sit here under Necromancer and Demonologist at level 80, which is only where it landed because
			// the corpus's two mount owners happen to be a level-80 Necro and a level-80 Demo - mount OWNERSHIP showing
			// through as class. So the assertion is zero classes, not "fewer than twelve": one row of it in
			// ClassAbility means the derivation has started reading ownership as class again.
			var mountAbilities = byName.Where(x => x.Name.StartsWith(Ability.MountAbilityNamePrefix, StringComparison.Ordinal)).Select(x => x.Name).Distinct().ToList();
			Assert.True(mountAbilities.Count == 0,
				$"mount abilities are in ClassAbility ({string.Join(", ", mountAbilities)}) - they hang off owning a mount, not off a class, and CharacterLoadout grants them from the character's mount summon spells.");
		}

		// A multi-goal quest's play ORDER is the client's `Phase`. `Sequence` is a WIRE value and must never be
		// renumbered to serve as one - see QuestGoalDefinition.Sequence, and QuestProgress.InPlayOrder, which is the
		// only way the runtime is allowed to ask.
		//
		// This gate is on the shipped DATA, not the code: it pins the five quests whose goals all carry `N` = 1
		// because their objectives are PARALLEL. They are what caught the mistake - renumbering them 1..n to make
		// them sortable rewrote bytes retail sent, and the objective-region golden test failed immediately.
		[Fact]
		public async Task ParallelGoalQuests_KeepTheirCapturedOrdinal()
		{
			var ct = TestContext.Current.CancellationToken;
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));

			foreach (var questId in new uint[] { 14020, 16004, 16183, 16888, 17350 })
			{
				var seqs = await db.QuestGoalDefinitions.AsNoTracking().Where(g => g.QuestDefinitionId == questId).Select(g => g.Sequence).ToListAsync(ct);
				Assert.True(seqs.Count > 1, $"quest {questId} should be multi-goal");
				Assert.True(seqs.TrueForAll(n => n == 1),
					$"quest {questId}'s goals carry ordinals [{string.Join(",", seqs)}] - retail numbers every goal of a PARALLEL quest 1, and rewriting them breaks the captured objective region.");
			}
		}

		// No goal may stay `Partial` (#527). A partial goal is skipped by AllGoalsComplete, MatchingKillGoal and
		// QuestObjectiveRegion.Build, so a multi-goal quest carrying one silently presents - and completes - as a
		// quest with fewer goals than it has. Both of the two that existed are now whole, so the shipped count is
		// zero and any new one has to be resolved rather than accumulating.
		[Fact]
		public async Task NoQuestGoal_IsStillPartial()
		{
			var ct = TestContext.Current.CancellationToken;
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var partial = await db.QuestGoalDefinitions.AsNoTracking().Where(g => g.Partial).Select(g => new { g.QuestDefinitionId, g.GoalId, g.Name }).ToListAsync(ct);

			Assert.True(partial.Count == 0,
				$"partial goals remain: {string.Join(", ", partial.Select(p => $"{p.QuestDefinitionId}/{p.GoalId} '{p.Name}'"))} - each is invisible to the progression engine.");
		}

		// The Acheronian Gate (12369), the quest the two above were found through - and the ONE shipped quest where
		// `Phase` and `Sequence` disagree, so it is also the gate on InPlayOrder actually being used. Its goals had
		// been wired to each
		// OTHER's objective: 3492 "Unlock the Acheronian gate" carried the key loot and 7134 "Find the gate key" was
		// left inert. The marker is what misled the original seed - Saddur spawns at 521/757 and the world object
		// "Acheronian Gate" stands at 523.4/754.7, ~3u apart - so the goal's own NAME is what settles which is which.
		[Fact]
		public async Task TheAcheronianGate_HasItsTwoGoalsTheRightWayRound()
		{
			var ct = TestContext.Current.CancellationToken;
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var goals = await db.QuestGoalDefinitions.AsNoTracking().Where(g => g.QuestDefinitionId == 12369).ToListAsync(ct);
			goals = QuestProgress.InPlayOrder(goals).ToList();

			Assert.Equal(2, goals.Count);

			// Find the gate key FIRST - it is the goal the corpus shows taking the `Advanced` hand-off.
			Assert.Equal(7134u, goals[0].GoalId);
			Assert.Equal(QuestGoalType.LootFromNpc, goals[0].GoalType);
			Assert.Equal("Saddur", goals[0].TargetName);
			Assert.Equal(193186u, goals[0].QuestItemId); // "Acheronian Gate Key"

			// Then unlock the gate. ReachArea at its own marker, which is where the gate object stands.
			Assert.Equal(3492u, goals[1].GoalId);
			Assert.Equal(QuestGoalType.ReachArea, goals[1].GoalType);
			Assert.Null(goals[1].TargetName);
			Assert.True(goals[1].MarkerX > 0 && goals[1].MarkerY > 0, "a ReachArea goal with no marker can never be completed");
		}
	}
}
