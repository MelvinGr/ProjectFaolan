using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
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
			Assert.True(discovery >= 11, $"{discovery} respawn points carry a discovery volume; the import resolves 11.");
			Assert.True(goals >= 3, $"{goals} quest goals carry a region; the import resolves 3.");

			var linked = (await db.RespawnPoints.Where(p => p.DiscoveryRegionId != null).Select(p => p.DiscoveryRegionId!.Value).ToListAsync(ct))
				.Concat(await db.QuestGoalDefinitions.Where(g => g.RegionId != null).Select(g => g.RegionId!.Value).ToListAsync(ct))
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

		// No GATHER objective is left at the fresh-harvest `Kill` default (#521). A goal whose trailing reference is an
		// item (LDB category 50200) and whose kind tag says it acquires one is a collect objective; they shipped as
		// `Kill` with no target, which is a quest that can never complete - the kill matcher looking for an NPC whose
		// template id equals an item id. `TypeItemGoalsFromTrailingCategory` types them, and this stops a re-harvest
		// reintroducing the inert form: the row count never moves, only the GoalType does.
		//
		// ⚠️ `Deliver` (kind 20) is EXCLUDED on purpose and must stay excluded. It hands the item over rather than
		// acquiring it, and the goal does not carry the recipient - typing it collect would complete the quest the
		// moment the player picks the item up, skipping the delivery. That is a silent chain bug; inert is at least
		// visibly stuck. This asserts the exclusion holds, so a future "tidy up the last inert goals" pass has to
		// deal with the recipient rather than just widening the rule.
		[Fact]
		public async Task ItemGoals_AreTypedByTheirKindTag()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var inert = await db
				.QuestGoalDefinitions.Where(g => g.TrailingCategory == 50200 && g.TrailingRefId > 0
				                                 && g.GoalType == QuestGoalType.Kill
				                                 && g.Separator != (uint)QuestGoalKind.Deliver)
				.Select(g => $"goal {g.GoalId} (quest {g.QuestDefinitionId}, kind {g.Separator})")
				.ToListAsync(ct);

			Assert.True(inert.Count == 0, $"acquire-kind item goals still typed as Kill: {string.Join(", ", inert)}");

			// ...and the typing actually happened rather than the category vanishing from the harvest.
			var collect = await db.QuestGoalDefinitions.CountAsync(g => g.TrailingCategory == 50200 && g.GoalType == QuestGoalType.CollectItem, ct);
			Assert.True(collect >= 60, $"only {collect} item goals are CollectItem; the harvest carries 62.");

			// The deliver goals are NOT quietly collect-typed.
			var mistyped = await db.QuestGoalDefinitions.CountAsync(
				g => g.Separator == (uint)QuestGoalKind.Deliver && g.GoalType == QuestGoalType.CollectItem, ct);
			Assert.True(mistyped == 0, $"{mistyped} Deliver-kind goals are typed CollectItem - they would complete without the hand-in.");
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

		// A `Deliver` goal is wired as a talk-to at the RECIPIENT named in its (decrypted) objective text, and the
		// recipient must be an NPC we actually hold. Wiring one we don't produces a goal that can never complete - no
		// better than the inert state it replaced, and worse in that it LOOKS wired.
		//
		// This is the invariant, not the current membership: 10 of the 12 recipients stand on maps with zero NpcSpawns,
		// so they stay unwired until those maps are imported, at which point the importer picks them up on its own.
		// Asserting "TargetName resolves" rather than "exactly these two are wired" is what lets that happen without
		// this test having to be edited.
		[Fact]
		public async Task DeliverGoals_AreOnlyWiredToRecipientsWeHave()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var wired = await db
				.QuestGoalDefinitions.Where(g => g.Separator == (uint)QuestGoalKind.Deliver && g.TargetName != null)
				.Select(g => new { g.GoalId, g.GoalType, Target = g.TargetName! })
				.ToListAsync(ct);

			var npcNames = await db.Set<NpcTemplate>().Select(t => t.Name).ToListAsync(ct);
			var haveNpc = npcNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

			var dangling = wired.Where(g => !haveNpc.Contains(g.Target)).Select(g => $"goal {g.GoalId} -> '{g.Target}'").ToList();
			Assert.True(dangling.Count == 0, $"Deliver goals wired to an NPC that is not in NpcTemplates: {string.Join(", ", dangling)}");

			// A hand-in advances on the TALK, so any other trigger would never fire (or, as CollectItem, would fire on
			// merely obtaining the item and skip the delivery).
			var wrongTrigger = wired.Where(g => g.GoalType != QuestGoalType.TalkToNpc).Select(g => $"goal {g.GoalId} is {g.GoalType}").ToList();
			Assert.True(wrongTrigger.Count == 0, $"Deliver goals with a target but not TalkToNpc: {string.Join(", ", wrongTrigger)}");

			// The two whose recipients we do hold, pinned by name so a re-harvest that dropped one is caught.
			Assert.Equal("Turach", wired.Single(g => g.GoalId == 3497).Target);
			Assert.Equal("Croiss", wired.Single(g => g.GoalId == 5792).Target);
		}

		// A shop with no wares is a shop the player cannot buy from - the exact shape of the loss above, and the one
		// a bare row count on ShopItems would still miss if a single vendor were emptied.
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
		// A shipped world object with NO display element never lifted, so WorldObjectBuilder refuses to build it and it
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
	}
}
