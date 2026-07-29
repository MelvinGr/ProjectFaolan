using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.World;
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
