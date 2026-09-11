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
	// The shipped seed actually carries its content.
	//
	// Every other gate asserts that content is correct; this one asserts that it is there - a floor under each table
	// the game is unplayable without, so a destructive migration or a broken import fails loudly instead of shipping
	// an empty world.
	//
	// The floors are deliberately well below the current counts: this is a canary for catastrophic loss, not a
	// ratchet that fights every legitimate data change. Runs against the fixture built from faolan.sql.
	public class SeedContentTests
	{
		// Conarch Armory - the class-specific instance behind Conarch Village's `Armory` door, and the only one any
		// capture has walked into (Hakotep as a Herald of Xotli, NecroL80 as a Necromancer).
		private const uint ConarchArmoryMapId = 5007;

		// The harvester's own leg-join tolerance (PatrolPathExtractor.LegJoinTolerance) - legs that are one beat
		// join within this, a leg that belongs elsewhere misses by far more.
		private const float PatrolLegJoinTolerance = 5f;

		// A reach objective needs somewhere to walk to. The runtime solves one by proximity to the goal's own map
		// marker, so a ReachArea goal with no marker can never be completed - and a quest that cannot be completed is
		// strictly worse than one that is visibly inert, because nothing says so.
		//
		// `MapId`/`MarkerX`/`MarkerY` are nullable, so a rule guarding on `goal.MapId == 0` lets a markerless goal
		// through - a silent break no row count would show.
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

		// The outlaw resurrection points survived the harvest. RespawnPoint.Shady comes from a single RDB attribute that
		// 222 of the exported elements omit entirely, so a parser or dialect regression does not fail loudly - it just
		// silently files every criminal point as a normal one, and the whole outlaw revive destination quietly becomes
		// "wherever a law-abiding player would go". A bare RespawnPoints row count cannot see that, which is precisely
		// why this asserts on the flag rather than the table.
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

			// The flag must agree with the designers' labels: no point named for the outlaw system may be filed normal.
			// The naming and the attribute agree exactly in the client's export.
			var misfiled = await db
				.RespawnPoints.Where(r => !r.Shady && (r.Name.Contains("Outlaw") || r.Name.Contains("Shady")))
				.Select(r => $"{r.Id} '{r.Name}'")
				.ToListAsync(ct);
			Assert.True(misfiled.Count == 0, $"Outlaw-named points filed as normal: {string.Join(", ", misfiled)}");
		}

		// The imported bounded-area regions and, more importantly, their links. A MapRegion nothing points at is dead
		// weight: the whole point of the table is that only regions a consumer resolves to are imported, so an unlinked
		// row means the resolution silently stopped working while the row count looked healthy.
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

		// A discovery volume is tighter than the 30-unit radius it replaces, asserted on the shipped data rather than in
		// prose. If a future harvest widened them, the "our radius is too generous" premise the column exists for would
		// be false and nobody would notice.
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

		// No gather objective is left at the fresh-harvest default. A goal whose trailing reference is an item (LDB
		// category 50200) and whose kind tag says it acquires one is a collect objective; un-typed, it is a quest that
		// can never complete. `TypeItemGoalsFromTrailingCategory` types them, and this stops a re-harvest reintroducing
		// the inert form: the row count never moves, only the GoalType does. The un-typed default is `None`, which is
		// what this half of the gate reads.
		//
		// The verbs where obtaining the item is not doing the objective are excluded on purpose and must stay excluded -
		// `Deliver` hands it over, `Craft` makes it, `Use` uses it on something, `DestroyObject` destroys a thing that
		// never enters the bag, and `Investigate` is a place you go and look at. Typing any of them collect completes the
		// quest the moment the item reaches the backpack, skipping the objective: a silent chain bug, where inert is at
		// least visibly stuck. Both halves of this gate carry the same list, so a future "tidy up the last inert goals"
		// pass has to answer the verb rather than just widening the rule - and so that this gate cannot end up asserting
		// the very mistyping the importer's rule removes.
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

			// ...and the typing actually happened rather than the category vanishing from the harvest.
			var collect = await db.QuestGoalDefinitions.CountAsync(g => g.TrailingCategory == 50200 && g.GoalType == QuestGoalType.CollectItem, ct);
			Assert.True(collect >= 42, $"only {collect} item goals are CollectItem; the harvest carries 42.");

			// ...and none of the five is quietly collect-typed.
			var mistyped = await db
				.QuestGoalDefinitions.Where(g => NotSatisfiedByArrival.Contains(g.Separator ?? 0) && g.GoalType == QuestGoalType.CollectItem)
				.Select(g => $"goal {g.GoalId} (kind {g.Separator})")
				.ToListAsync(ct);

			Assert.True(mistyped.Count == 0, $"goals whose verb is not satisfied by the item arriving are typed CollectItem: {string.Join(", ", mistyped)}");
		}

		// A goal is typed `Kill` because the client called it a kill, and for no other reason.
		//
		// `QuestGoalType.Kill` must never be enum value 0, which is also the un-typed default: 7,148 catalog goals carry
		// no verb and no target, and as kill goals every one of them makes `QuestProgress.MatchingKillGoal` false-fire.
		// Making Kill the default, or adding a rule that types a goal Kill for any reason but its verb, puts that whole
		// class of false-fire back silently, so it is worth a gate rather than a comment.
		//
		// The escape hatch is deliberate and narrow: a goal with a `TargetName` was configured by hand (a Seed* or the
		// Quest editor), and a human naming the NPC to kill is evidence in its own right - it is how the beach and
		// Casilda chains are wired. A goal with neither the verb nor a hand-set target has no evidence at all.
		[Fact]
		public async Task KillGoals_AreTypedFromTheClientsOwnVerb()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var kills = await db
				.QuestGoalDefinitions.Where(g => g.GoalType == QuestGoalType.Kill)
				.Select(g => new { g.GoalId, g.QuestDefinitionId, g.Separator, g.TargetName, g.TargetTemplateId, g.Name })
				.ToListAsync(ct);

			// The evidence is the client's own kill verb - as the wire's kind tag, or as the objective sentence
			// (TypeKillGoalsFromCatalogName). A hand-seeded target is the third and last way.
			var unevidenced = kills
				.Where(g => g.Separator != (uint)QuestGoalKind.Kill
				            && !QuestObjectiveText.KillObjective.IsMatch(g.Name ?? "")
				            && g.TargetName == null)
				.Select(g => $"goal {g.GoalId} (quest {g.QuestDefinitionId}, kind {g.Separator})")
				.ToList();

			Assert.True(unevidenced.Count == 0,
				$"goals typed Kill with neither the client's kill verb nor a hand-set target: {string.Join(", ", unevidenced)}");

			// The safety property of the sentence rule: a catalog-only kill goal's target must resolve to exactly one
			// creature we hold. A name we do not hold would be stored and never match anything - inert dressed up as
			// wired - and a name several templates share does not say which creature the quest meant (three distinct
			// "Scavenger Lackey" templates is the known case).
			//
			// ...unless the goal already carries a `TargetTemplateId`, which is the id half of that pair and says outright
			// which creature the quest meant - the name is only its fallback. A resolved goal is not ambiguous however many
			// templates share its name, so requiring name-uniqueness of it tests the wrong thing.
			var names = await db.NpcTemplates.Where(t => t.Name != null).Select(t => t.Name!).ToListAsync(ct);
			var owners = names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

			var unresolvable = kills
				.Where(g => g.Separator == null && g.TargetName != null && g.TargetTemplateId == null)
				.Where(g => !owners.TryGetValue(g.TargetName!, out var n) || n != 1)
				.Select(g => $"goal {g.GoalId} targets '{g.TargetName}'")
				.ToList();

			Assert.True(unresolvable.Count == 0,
				$"catalog-only kill goals whose target is not exactly one creature we hold: {string.Join(", ", unresolvable)}");

			Assert.True(kills.Count(g => g.Separator == null) >= 45,
				$"only {kills.Count(g => g.Separator == null)} catalog-only kill goals are typed; the sentence rule types 45.");

			// ...and the typing ran, rather than the verb vanishing from the harvest. All 30 kill-verb goals are typed:
			// the 24 naming an NPC template (51000) and the 6 naming a monster category (51100), which are typed and
			// inert because the group resolves to no NPC we hold.
			var byVerb = await db.QuestGoalDefinitions.CountAsync(g => g.Separator == (uint)QuestGoalKind.Kill && g.GoalType == QuestGoalType.Kill, ct);
			Assert.True(byVerb >= 30, $"only {byVerb} kill-verb goals are typed Kill; the harvest carries 30.");

			// The other half of the same rule, on the runtime side: MatchingKillGoal only matches TrailingRefId against a
			// killed entity's template id when the goal's own typed reference says it holds one. Nothing may be typed
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

		// A `TargetName` is a match key - it is compared for equality against the live entity's name - so a name we hold
		// must be stored the way the catalog spells it.
		//
		// Prose uses the short form where the world uses the full one: *"Talk to Bronwith in Conarch Village"* names
		// `Bronwith`, which can never equal the live **Battlemaster Bronwith**, and *"Speak to Chael"* names `Chael`
		// against **Tanner Chael**. Storing the prose form leaves a goal that reads as wired and can never fire, which is
		// why `RdbQuestGoalTargetImporter` lets the catalog win wherever it exists.
		//
		// A target we do not hold is fine and expected (33 of 57): the name is the client's own statement of who the
		// goal is about, and the goal starts working the day its map is imported. What must never happen is a target
		// that is *nearly* a held NPC.
		[Fact]
		public async Task ATargetName_IsSpeltTheWayTheCatalogSpellsIt()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var targets = await db
				.QuestGoalDefinitions.Where(g => g.TargetName != null && g.TargetName != "")
				.Select(g => new { g.GoalId, Target = g.TargetName! })
				.ToListAsync(ct);
			var npcs = await db.NpcTemplates.Where(t => t.Name != null).Select(t => t.Name!).Distinct().ToListAsync(ct);

			Assert.NotEmpty(targets);

			var shortForms = targets
				.Where(g => !npcs.Contains(g.Target, StringComparer.OrdinalIgnoreCase)
				            && npcs.Any(n => n.EndsWith(' ' + g.Target, StringComparison.OrdinalIgnoreCase)))
				.Select(g => $"goal {g.GoalId} '{g.Target}' vs live '{npcs.First(n => n.EndsWith(' ' + g.Target, StringComparison.OrdinalIgnoreCase))}'")
				.ToList();

			Assert.True(shortForms.Count == 0,
				$"TargetNames that are a SHORT FORM of an NPC we hold, so the equality match can never fire: {string.Join(", ", shortForms)}");
		}

		// A goal flagged `GatesTargetVisibility` or `TargetStaysDead` must have resolved to a template, because those
		// flags are the only input to `GetQuestGatedNpcMap` / `GetStoryKillTargets` and both are keyed by template. An
		// unresolved flagged goal is not a smaller gate - it is no gate: the NPC stops being hidden and
		// stops being NoRespawn, so a one-time story kill target respawns and is visible to everyone, silently. The
		// flag would read as set and do nothing.
		//
		// This is cheap to satisfy - both flagged goals carry a 51000 reference, so the rule's strongest source already
		// resolves them - and it is exactly the case where a silent failure is most expensive, since nothing in-client
		// distinguishes "not gated" from "gated and you qualify".
		[Fact]
		public async Task EveryGatedGoal_ResolvesToATemplate()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var flagged = await db
				.QuestGoalDefinitions.Where(g => g.GatesTargetVisibility || g.TargetStaysDead)
				.Select(g => new { g.GoalId, g.TargetName, g.TargetTemplateId })
				.ToListAsync(ct);

			Assert.NotEmpty(flagged);

			var unresolved = flagged.Where(g => g.TargetTemplateId is null or 0).Select(g => $"goal {g.GoalId} '{g.TargetName}'").ToList();

			Assert.True(unresolved.Count == 0,
				$"gated / stays-dead goals with no TargetTemplateId - the flag is silently inert on these: {string.Join(", ", unresolved)}");
		}

		// A resolved `TargetTemplateId` must agree with the goal's own typed reference wherever the goal carries one.
		// The two are independent statements of the same fact - the client's 51000 `npc_template.name` ref, and a lookup
		// of the name that ref's LDB string produced - so a disagreement means the resolution rule drifted off the
		// evidence rather than reading it.
		//
		// It stays a gate rather than a one-off measurement because the rule has three sources and the other two are
		// inferential: this is the only place the inference can be checked against something the client actually said.
		// place the inference can be checked against something the client actually said.
		[Fact]
		public async Task AResolvedTargetTemplate_AgreesWithTheGoalsOwnReference()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var disagreements = await db
				.QuestGoalDefinitions.Where(g => g.TargetTemplateId != null
				                                 && g.TrailingCategory == QuestProgress.NpcTemplateNameCategory && g.TrailingRefId > 0
				                                 && g.TargetTemplateId != g.TrailingRefId)
				.Select(g => $"goal {g.GoalId} resolved {g.TargetTemplateId} but its 51000 ref says {g.TrailingRefId}")
				.ToListAsync(ct);

			Assert.True(disagreements.Count == 0,
				$"TargetTemplateId contradicting the goal's own npc_template reference: {string.Join(", ", disagreements)}");
		}

		// The delivery objectives are not typed from their sentence, and that has to stay true.
		//
		// They are the larger set by far - Give 656, Hand 504, Return 385, Deliver 309, against Talk 473 / Speak 242 /
		// Report 225 - so the temptation to sweep them up with the talk goals is real and recurring. A delivery hands
		// an item over, so it must be gated on the player holding it: `QuestProgress.CanHandIn` keys on the wire kind
		// (`Separator` = Deliver) and on the item the trailing reference names. A catalog-only goal has neither, so
		// typing one `TalkToNpc` completes it on a bare greeting with the goods still in the shop.
		//
		// Scoped to goals never seen on the wire (`Separator` null): the set with no kind tag and no trailing reference,
		// which is exactly what the sentence rules reach. A goal the wire has shown carries its kind and the `Deliver`
		// machinery owns it - `IsDelivery` reads those two captured fields.
		//
		// The invariant is not "no hand-in is typed" - it is "a typed hand-in is gateable". `TypeHandInGoalsFromCatalogName`
		// types the nine whose sentence resolves both ends and records the item in `QuestItemId`, which is precisely
		// what `CanHandIn` reads. What must never appear is a hand-in typed `TalkToNpc` with neither.
		//
		// (One captured hand-in is genuinely ungated and stays that way, which is why the scope above matters: goal 400
		// "Give Laranga's letter to Tina" is a `Deliver` whose wire record carries no trailing reference - the corpus
		// only ever full-updates it - and "Laranga's letter" is not in `Items` either, so gating on it would make the
		// Shipwrecked chain uncompletable rather than merely un-gated.)
		[Fact]
		public async Task DeliveryObjectives_AreOnlyTypedWhenTheHandInCanBeGated()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var handIns = await db
				.QuestGoalDefinitions.Where(g => g.GoalType == QuestGoalType.TalkToNpc && g.Separator == null && g.Name != null
				                                 && (g.Name.StartsWith("Give ") || g.Name.StartsWith("Hand ")
				                                     || g.Name.StartsWith("Deliver ") || g.Name.StartsWith("Return ")
				                                     || g.Name.StartsWith("Bring ")))
				.Select(g => new { g.GoalId, g.QuestItemId, Name = g.Name! })
				.ToListAsync(ct);

			var ungated = handIns.Where(g => g.QuestItemId is null or 0).Select(g => $"goal {g.GoalId} '{g.Name}'").ToList();
			Assert.True(ungated.Count == 0,
				"catalog-only hand-in goals typed TalkToNpc with no QuestItemId for CanHandIn to gate on "
				+ $"- they complete on a bare greeting: {string.Join(", ", ungated)}");

			// ...and every item they do name is a real catalog row, or the gate tests an item nobody can hold and the
			// quest becomes uncompletable instead of merely un-gated.
			foreach (var handIn in handIns)
				Assert.True(await db.Items.AnyAsync(i => i.Id == handIn.QuestItemId, ct),
					$"goal {handIn.GoalId} '{handIn.Name}' hands over item {handIn.QuestItemId}, which is not in the catalog.");

			// ...and both sentence rules really did run, so none of this is vacuous.
			Assert.True(handIns.Count >= 9, $"only {handIns.Count} catalog-only hand-in goals are typed; the rule types 9.");
			var talk = await db.QuestGoalDefinitions.CountAsync(g => g.GoalType == QuestGoalType.TalkToNpc && g.Separator == null, ct);
			Assert.True(talk >= 700, $"only {talk} catalog-only goals are typed TalkToNpc; the sentence rules type 762.");
		}

		// A goal that asks for N states N in its own objective sentence, and it must be stored.
		//
		// `RequiredCount` otherwise comes only from the wire goal record, so a goal no capture has shown sits at 0 -
		// and the runtime reads 0 as one (`Math.Max(RequiredCount, 1)`). 58 shipped goals are reachable only through the
		// sentence, 28 of them asking for more than one: without it, *"Hand in 20 Yew"* completes on the first log with
		// the journal showing 1/1.
		//
		// The client's quest catalog is the only source for these counts, so a regression here restores a bug nothing
		// else can see - the quest looks finished because as far as the server knows it is.
		[Fact]
		public async Task GoalsThatStateAnAmount_CarryIt()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var named = await db
				.QuestGoalDefinitions.Where(g => g.Name != null && g.Name != "")
				.Select(g => new { g.GoalId, g.RequiredCount, g.Separator, Name = g.Name! })
				.ToListAsync(ct);

			var missing = named
				.Where(g => QuestObjectiveText.StatedCount(g.Name) is { } n and > 1 && g.RequiredCount < n)
				.Select(g => $"goal {g.GoalId} '{g.Name}' asks for {QuestObjectiveText.StatedCount(g.Name)} but stores {g.RequiredCount}")
				.ToList();

			Assert.True(missing.Count == 0,
				$"goals whose objective states an amount larger than their RequiredCount - they complete early: {string.Join(", ", missing)}");

			// ...and the rule ran. 28 of the 58 ask for more than one.
			var counted = named.Count(g => QuestObjectiveText.StatedCount(g.Name) is > 1 && g.RequiredCount > 1);
			Assert.True(counted >= 28, $"only {counted} goals carry a stated amount above 1; the catalog states 28.");

			// And the two the wire also carries must still match it - `RequiredCount` is written back out in the
			// byte-exact @609 region, so a sentence-read value overwriting a captured one breaks a golden test. The
			// rule is scoped to catalog-only goals precisely to make that impossible.
			foreach (var wire in named.Where(g => g.Separator != null && QuestObjectiveText.StatedCount(g.Name) is not null))
				Assert.True(QuestObjectiveText.StatedCount(wire.Name) == wire.RequiredCount,
					$"goal {wire.GoalId} '{wire.Name}': the sentence says {QuestObjectiveText.StatedCount(wire.Name)}, the capture says {wire.RequiredCount}.");
		}

		// A goal's own sentence outranks its map marker.
		//
		// The marker is the weakest evidence a goal carries, so `TypeLocationGoalsFromTrailingCategory` must not read it
		// first: *"Speak to Chulainn"* has a pin at Chulainn's feet and *"Escort Casilda to the city"* has one where
		// Casilda stands when the escort is granted, so typing either `ReachArea` completes it by walking up to the NPC -
		// the talk goal without its conversation ever playing, the escort the instant it begins.
		//
		// This gate reads the shipped rows rather than the rule: it is the state a clean rebuild has to keep reproducing,
		// and a favourable ordering plus the bootstrap guard can otherwise hide a broken rule.
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
		// all the pair that proves the tag describes the verb and not the target: quest 12371's two goals reference the
		// same item with the same count, and are 23 (collect) and 20 (deliver).
		[Fact]
		public async Task GoalKindTag_AgreesWithTheHandWiredGoals()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;
			var goals = await db.QuestGoalDefinitions.Where(g => g.GoalId == 1448 || g.GoalId == 3490 || g.QuestDefinitionId == 12371).ToListAsync(ct);

			var collect = goals.Single(g => g.GoalId == 3495);
			var deliver = goals.Single(g => g.GoalId == 3497);
			Assert.Equal(collect.TrailingRefId, deliver.TrailingRefId); // the same item...
			Assert.Equal(collect.RequiredCount, deliver.RequiredCount); // ...and the same count...
			Assert.Equal((uint)QuestGoalKind.Collect, collect.Separator); // ...but different kinds
			Assert.Equal((uint)QuestGoalKind.Deliver, deliver.Separator);

			Assert.Equal((uint)QuestGoalKind.RegionScoped, goals.Single(g => g.GoalId == 1448).Separator);
			Assert.Equal((uint)QuestGoalKind.Kill, goals.Single(g => g.GoalId == 3490).Separator);
		}

		// Every goal whose trailing reference is an npc_template (LDB category 51000) names its target, and where we
		// hold that template the name is the catalog's - i.e. the exact string the runtime compares against
		// (QuestProgress.MatchingKillGoal's name fallback, QuestMarkerResolver.MarkerStateFor).
		//
		// LDB[51000] is keyed by template id and carries the client's own name, but 27 of our 1,018 templates were
		// captured under the shared placeholder `Halen van Dummy`, and for those the LDB's real name would look resolved
		// and never match. So the catalog wins wherever it exists; the LDB fills only the templates we do not hold,
		// which are inert until their map is imported.
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

		// A quest-gated kill target must be killable enough times to finish its goal. The gate forces NoRespawn (a
		// story target stays dead), so the world has to hold at least `RequiredCount` spawns of that name - otherwise
		// the goal sticks one short with nothing left alive and the quest is permanently uncompletable.
		//
		// Membership must never be derived from "a Kill goal that has a TargetName": that holds for the uniquely-named
		// story targets and fails the moment target names are filled mechanically (quest 12057 asks for 9 Vanir Polluter
		// kills against 8 spawns in the world). `GatesTargetVisibility` is an explicit, hand-set column, and this asserts
		// the property that makes setting one safe rather than the current membership.
		[Fact]
		public async Task GatedKillTargets_HaveEnoughSpawnsToFinishTheGoal()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			// The flag that makes the kill count a hard cap is `TargetStaysDead`, not the visibility gate - a gated
			// target that respawns can always be killed again. So this reads the respawn column.
			var gated = await db.QuestGoalDefinitions.Where(g => g.TargetStaysDead).ToListAsync(ct);
			Assert.NotEmpty(gated); // the two story targets; an empty set would mean the flag was lost, not that it is unused

			var spawnsByName = await db
				.NpcSpawns.GroupBy(s => s.NpcTemplate.Name)
				.Select(grp => new { Name = grp.Key, Count = grp.Count() })
				.ToDictionaryAsync(x => x.Name, x => x.Count, ct);

			foreach (var g in gated)
			{
				Assert.False(string.IsNullOrEmpty(g.TargetName), $"goal {g.GoalId} kills a one-time target but names no target");
				var spawns = spawnsByName.GetValueOrDefault(g.TargetName!, 0);
				var needed = Math.Max(1, (int)g.RequiredCount);
				Assert.True(spawns >= needed,
					$"goal {g.GoalId} (quest {g.QuestDefinitionId}) needs {needed} kill(s) of '{g.TargetName}' but the world holds {spawns} spawn(s), " +
					"and a TargetStaysDead target does not respawn - the quest can never complete.");
			}
		}

		// A trigger-spawned type is never a world placement. Two kinds today: a `NpcAmbush` ambusher (a quest goal
		// advancing spawns it) and a `NpcEncounter` boss (a proximity lure swaps into it). Neither stands anywhere, so
		// a `NpcSpawns` row for one is a harvest of the trigger firing, and rendering it puts a creature in the world
		// permanently that retail only ever shows to a player who triggered it.
		//
		// `NpcImportOps.BuildBurstTruthSpawns` excludes both by rule. Position dedup is not a substitute: an encounter
		// boss spawning on the exact spot her lure vacated is dropped by it only until a capture catches the swap a few
		// units off.
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

		// No shipped goal carries kind 0, because the wire never sends one. The tag takes 5/9/10/12/20/22/23/24/25
		// across all 158 snapshot goals and all 111 full-update ones, and 0 is not among them: a row holding 0 is not a
		// kind at all but an un-harvested default, which reads as an untargeted verb-less objective.
		//
		// A regression gate rather than a decode claim: a re-harvest that stops lifting the tag repopulates that phantom
		// silently.
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

			// ...and the tag is only ever a kind the corpus actually shows, so a stray value is caught too. Null is
			// excluded rather than asserted against: it means "never seen on the wire", which is every goal that comes
			// from the client's quest catalog rather than a capture (see QuestGoalDefinition.Separator).
			var known = Enum.GetValues<QuestGoalKind>().Select(k => (uint)k).ToHashSet();
			var stray = await db.QuestGoalDefinitions.Where(g => g.Separator != null).Select(g => g.Separator!.Value).Distinct().ToListAsync(ct);
			Assert.All(stray, s => Assert.Contains(s, known));
		}

		// The quest index's per-line catalog may never contain a quest the captures never showed.
		//
		// The index block lists its line's quest ids plus a (sequence, remaining) pair built from the line's count, so
		// the 3011 quests that come from the client's quest catalog (RDB 1000104) rather than a capture would change
		// that region for the 56 snapshot-eligible quests sharing a line with one. The goldens cannot catch it: the only
		// index golden covers quest 10238, whose line 241 is complete either way.
		//
		// It goes through the repository on purpose. The filter lives in GetQuestDefinitionsByLine, so a test that
		// re-implements the query would pass no matter what the repository does.
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

		// The two quests that carry a captured line catalog (IndexQuestLineQuestIds) are a byte-level oracle for the
		// list the index builder derives from the DB: the CSV is what the wire actually sent.
		//
		// Necessary but not sufficient: both sit in lines (241 and 1054) the client's quest catalog does not grow, so
		// this locks the derivation only for the two lines we can prove.
		// QuestIndexCatalog_HoldsOnlyQuestsTheCapturesShowed covers the other 80.
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

		// Every server-opened conversation is reachable: it has a trigger the runtime can fire, and an NPC to speak it.
		//
		// The two kinds are complementary and exhaustive - a world object (use it) or an approach (walk up to the NPC) -
		// so a node with neither is dead data: imported, keyed, and unreachable by any code path.
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
				// The trigger is what keeps these out of the talk path: the runtime looks a root up by trigger, so a
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

		// The escort can actually end. Its completion is data-driven off the escortee's server-opened conversation:
		// using the world object that plays her farewell is what completes the goal, so the chain closes only if the
		// shipped data still connects escort goal -> escortee -> a server-initiated node with a trigger.
		//
		// Worth a gate because the link spans three tables and a re-harvest could quietly break any leg of it - and the
		// symptom would be an escort that silently never ends.
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

		// A `Deliver` goal hands its item over by talking to the recipient, so the trigger must be TalkToNpc - never
		// CollectItem, which would complete the quest the moment the player picks the item up and skip the delivery.
		//
		// It deliberately does not require the recipient to be an NPC we hold. The name comes from the client's own
		// objective text, the goal is inert either way, and recording it means the goal works the moment its map is
		// imported. Coverage is preferred over precision here; what is not allowed is a wrong trigger, which would
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

			// The two whose recipients we do hold are the playable ones, pinned by name so a re-harvest that dropped or
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

		// Every shipped patrol rebuilds into a wire task. This reads the committed rows, which is what
		// PatrolNodeRoundTripTests cannot see: that one converts a captured record in memory (parse -> ToNode ->
		// ToTask) and never touches a row, so an importer that fails to persist a column still looks healthy. A null
		// column is fatal at runtime - `ToTask` dereferences PathTrailerWord on every mode-1 node, so a Path patrol
		// throws inside EnsureZoneNpcs and aborts world entry, leaving a half-built zone with no NPCs.
		//
		// ToTask throws loudly on a missing column by design (a patrol that cannot be rebuilt byte-exact must not be
		// silently emitted short), which makes simply calling it the whole assertion.
		// A shipped world object with no display element never lifted, so WorldObjectCodec refuses to build it and it
		// silently never spawns - dead weight in the seed. The grammar side of this is WorldObjectCorpusLiftTests; this
		// is the seed side, and it also catches a row left stale by a lift fix, since lifting changes the derived id and
		// the old row is not deleted.
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

		// Every shipped route must be one continuous polyline. The runtime walks a route waypoint by waypoint, so a
		// break in it is not a gap the NPC skips - it is walked, as a straight line between the two sides, through
		// whatever stands there (Old Tarantia's guards crossing the buildings is the symptom).
		//
		// A captured task carries the NPC's current short step as well as the beat it is walking, as two mode-1 legs of
		// one mode-4 list. Where they join (median 0.7u) they are genuinely one path; where they don't, concatenating
		// them fabricates a chord of up to 495.9u. PatrolPathExtractor.TrimToContinuousRoute drops the non-joining leg
		// at harvest; this is the seed side of that.
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

		// A class-specific map (the city armories) holds a different NPC set per class, so every spawn on one must say
		// which class it belongs to. A row left class-agnostic there is served to all twelve classes on top of their
		// own set, so an armory shows one class's quartermasters to everybody.
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

		// The Conarch Armory is the capture-proven case, and the shape is specific: the same five quartermaster posts
		// hold a different NPC per class (Zahur/Sabola, Akila/Imogen, Ebo/Chuma, Pancras/Ofir at identical positions),
		// which is why the class is part of the spawn's unique index and its derived key. Without it the second class's
		// import collides with the first and one whole set is silently dropped.
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

			// At least one post is genuinely shared between two classes with a different NPC on it. This is the
			// assertion a (map, position) key cannot survive.
			var contested = spawns.GroupBy(s => (s.PositionX, s.PositionZ)).Any(g => g.Select(s => s.NpcTemplateId).Distinct().Count() > 1);
			Assert.True(contested, "No armory post is held by a different NPC per class - the two class sets are no longer sharing positions, so this gate no longer proves anything.");
		}

		// The ambient barks harvested from the client's LDB are in the seed, and the two-hander rule holds.
		//
		// A capture only catches the lines an NPC happened to say while someone was recording, so the importer fills in
		// the rest of each captured group's conversation from category 50015 - which it can do because that category is
		// plaintext and a line's id is computable from its token. The harvested rows are identifiable by their empty
		// FormatKey: a key is only read for an encrypted string, so a harvested line legitimately has none.
		//
		// The second assertion is the one that matters. `RenownBystanderLovers` is one conversation shared by the
		// Nobleman and the Noblewoman, and its lines interleave the two speakers - so harvesting it would put her lines
		// in his mouth. If a keyless Lovers row ever appears, the single-speaker guard has been lost.
		//
		// The third guard is that the harvest only fills ambient channels. A conditional channel's siblings belong to
		// the script's states: the crier's `sendtell` set is 18 lines, 13 of them stages of a guild-advertising
		// transaction, and a slot-free filter over it selects "Excuse me, I wanted to ask you something." as a greeting.
		[Fact]
		public async Task HarvestedBarks_AreInTheSeed_AndNoTwoHanderGroupWasHarvested()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var barks = await db.NpcChatter.AsNoTracking().ToListAsync(ct);
			var harvested = barks.Where(b => b.FormatKey.IsEmpty).ToList();

			Assert.True(harvested.Count >= 8, $"The seed carries {harvested.Count} harvested bark(s); the sweep yields 8 across the single-speaker AMBIENT groups.");
			Assert.True(barks.Count > harvested.Count, "Every bark is keyless - the CAPTURED lines have lost their keys.");

			// The two-hander must never be harvested onto either speaker.
			var lovers = harvested.Where(b => b.Group == "RenownBystanderLovers").ToList();
			Assert.True(lovers.Count == 0,
				$"{lovers.Count} line(s) of the shared Nobleman/Noblewoman conversation were harvested - the single-speaker guard is gone, so one speaker is now saying the other's lines.");

			// A harvested line must never carry a template slot: its wire form would need a <parameter> nobody fills.
			Assert.All(harvested, b => Assert.DoesNotContain("{", b.Group, StringComparison.Ordinal));

			// ...and never a conditional channel, where the script's state picks the line rather than a random draw.
			Assert.All(harvested, b => Assert.DoesNotContain(b.Channel, new[] { "sendtell", "eventactions", "event" }));
		}

		// The abilities a levelled character earns reach all twelve classes, not just the four the corpus has a levelled
		// character for.
		//
		// This is a gate on a corpus hole, which is why a row count cannot stand in for it. Every gameplay ability is
		// harvested from a per-character grant, and the corpus has exactly four levelled characters (Assassin 23,
		// HoX 24, Necro 80, Demo 80) - so without the universal pass, Search / Hide / Path of Asura land on those four
		// classes and a level-80 Barbarian has no search, no hide and no recall, which reads as a design decision in the
		// data and is really just "nobody captured a Barbarian past level 1".
		//
		// The other half matters as much: the pass must not over-generalise. Spellweaving, the pet bar and the mount
		// abilities are all held by exactly the two level-80 casters, and each is independently known not to be
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

			// Universal but not part of the level-1 create kit - the three the levelled roster agrees on.
			foreach (var ability in new[] { "Search", "Hide", "Path of Asura" })
				Assert.True(classCount.TryGetValue(ability, out var n) && n == 12,
					$"'{ability}' reaches {(classCount.TryGetValue(ability, out var m) ? m : 0)} of 12 classes - it is earned by every levelled class the corpus has, so withholding it from the rest is a capture artefact.");

			// Class kit, and it must stay class kit.
			foreach (var ability in new[] { "Spellweaving", "Pet Attack", "Pet Dismiss" })
				Assert.True(!classCount.TryGetValue(ability, out var n) || n < 12,
					$"'{ability}' reaches all 12 classes - it is held by exactly the two captured level-80 casters, so the universal-witness threshold has been weakened into treating a caster pair as the whole roster.");

			// A mount ability is not class kit at all, not even narrow class kit: it comes with the mount. The corpus's two
			// mount owners are a level-80 Necromancer and a level-80 Demonologist, so a derivation reading mount ownership
			// as class files it under those two. The assertion is therefore zero classes, not "fewer than twelve": one row
			// of it in ClassAbility means ownership is being read as class again.
			var mountAbilities = byName.Where(x => x.Name.StartsWith(Ability.MountAbilityNamePrefix, StringComparison.Ordinal)).Select(x => x.Name).Distinct().ToList();
			Assert.True(mountAbilities.Count == 0,
				$"mount abilities are in ClassAbility ({string.Join(", ", mountAbilities)}) - they hang off owning a mount, not off a class, and CharacterLoadout grants them from the character's mount summon spells.");
		}

		// A multi-goal quest's play order is the client's `Phase`. `Sequence` is a wire value and must never be
		// renumbered to serve as one - see QuestGoalDefinition.Sequence, and QuestProgress.InPlayOrder, which is the
		// only way the runtime is allowed to ask.
		//
		// This gate is on the shipped data, not the code: it pins the five quests whose goals all carry `N` = 1 because
		// their objectives are parallel. Renumbering them 1..n to make them sortable rewrites bytes retail sent.
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

		// No goal may stay `Partial`. A partial goal is skipped by AllGoalsComplete, MatchingKillGoal and
		// QuestObjectiveRegion.Build, so a multi-goal quest carrying one silently presents - and completes - as a
		// quest with fewer goals than it has. The shipped count is zero, so any new one has to be resolved rather than
		// accumulating.
		[Fact]
		public async Task NoQuestGoal_IsStillPartial()
		{
			var ct = TestContext.Current.CancellationToken;
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var partial = await db.QuestGoalDefinitions.AsNoTracking().Where(g => g.Partial).Select(g => new { g.QuestDefinitionId, g.GoalId, g.Name }).ToListAsync(ct);

			Assert.True(partial.Count == 0,
				$"partial goals remain: {string.Join(", ", partial.Select(p => $"{p.QuestDefinitionId}/{p.GoalId} '{p.Name}'"))} - each is invisible to the progression engine.");
		}

		// The Acheronian Gate (12369) is the one shipped quest where `Phase` and `Sequence` disagree, so it is also the
		// gate on InPlayOrder actually being used. Its two goals are easy to swap: Saddur spawns at 521/757 and the
		// world object "Acheronian Gate" stands at 523.4/754.7, ~3u apart, so the markers do not distinguish them and
		// the goal's own name is what settles which is which.
		[Fact]
		public async Task TheAcheronianGate_HasItsTwoGoalsTheRightWayRound()
		{
			var ct = TestContext.Current.CancellationToken;
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var goals = await db.QuestGoalDefinitions.AsNoTracking().Where(g => g.QuestDefinitionId == 12369).ToListAsync(ct);
			goals = QuestProgress.InPlayOrder(goals).ToList();

			Assert.Equal(2, goals.Count);

			// Find the gate key first - it is the goal the corpus shows taking the `Advanced` hand-off.
			Assert.Equal(7134u, goals[0].GoalId);
			Assert.Equal(QuestGoalType.LootFromNpc, goals[0].GoalType);
			Assert.Equal("Saddur", goals[0].TargetName);
			Assert.Equal(193186u, goals[0].QuestItemId); // "Acheronian Gate Key"

			// Then unlock the gate - by using the gate object, holding the key.
			//
			// Not `ReachArea`, even though the gate object does stand on the goal's marker: a reach would complete the
			// objective the moment the player walked up to the gate, before the key was looted and without the gate ever
			// being touched. "The marker is where the target is" makes a reach look right for any goal whose target is a
			// thing you interact with; a reach is the right type only when arriving is the objective. Retail sends the use
			// and ticks the goal after it.
			Assert.Equal(3492u, goals[1].GoalId);
			Assert.Equal(QuestGoalType.UseWorldObject, goals[1].GoalType);
			Assert.Null(goals[1].TargetName);
			Assert.Equal(975369919u, goals[1].TargetWorldObjectId); // the "Acheronian Gate" world object
			Assert.Equal(193186u, goals[1].QuestItemId); // ...which needs the key in hand to open
		}
	}
}
