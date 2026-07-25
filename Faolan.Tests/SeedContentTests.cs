using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
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
	}
}
