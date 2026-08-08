using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Progression;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// Resource harvesting (#494). The nodes themselves already shipped as WorldObjects and already streamed to the
	// client; what was missing was the payoff, which the capture shows is the ordinary item path. These gate the two
	// halves that can silently rot: the catalog agreeing with the shipped item rows, and the shipped nodes all being
	// covered by it.
	public class HarvestCatalogTests
	{
		// The three rows a capture actually measured, each landing beside its own annotation. If the item ids drift,
		// the identification is what broke.
		//
		// ⚠️ The counts here used to be 40 / 60 / 40, read off the 4th u32 of the DispatchDisplayItemLootChatMessage
		// that follows a harvest. That word is the item's LEVEL, not a count - and Items.Level for Granite, Oak and
		// Iron is 40, 60 and 40, which is why the misread looked like three independent measurements. The count is on
		// the ADD, in stat_list 26, and it is 1 on every captured harvest in both mining captures.
		[Theory]
		[InlineData("Granite Rock", 161206u)] // `just mined`
		[InlineData("Oak Tree", 161213u)] // `gethering wood` - the tree the maintainer chopped down
		[InlineData("Iron Node", 161196u)] // `mining iron`
		public void TheMeasuredNodes_YieldOneUnitPerSwing(string node, uint itemId)
		{
			Assert.Equal(itemId, HarvestCatalog.ResourceFor(node));
			Assert.Equal(HarvestCatalog.YieldPerSwing, HarvestCatalog.YieldFor(node));
		}

		// The level of each node's resource - the value the old yield table had actually captured. Kept as a gate so
		// the coincidence that made the misread convincing cannot quietly come back as a "measurement".
		[Theory]
		[InlineData(161206u, 40)]
		[InlineData(161213u, 60)]
		[InlineData(161196u, 40)]
		public async Task TheOldYieldNumbers_AreTheResourcesItemLevel(uint itemId, int level)
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(Support.TestPaths.FaolanDb));
			var item = await db.Items.FindAsync([itemId], TestContext.Current.CancellationToken);
			Assert.NotNull(item);
			Assert.Equal(level, item!.Level);
		}

		// An unmeasured node still yields its resource, at the documented default rather than at 0 (which would look
		// like a successful harvest that gave nothing).
		[Fact]
		public void AnUnmeasuredNode_YieldsItsResourceAtTheDefaultCount()
		{
			Assert.Equal(161218u, HarvestCatalog.ResourceFor("Yew Tree"));
			Assert.Equal(HarvestCatalog.DefaultYield, HarvestCatalog.YieldFor("Yew Tree"));
			Assert.True(HarvestCatalog.DefaultYield > 0, "a default yield of 0 would be a harvest that grants nothing");
		}

		// A full node is exactly ten swings: 1000 max, 100 a swing. Both halves are measured, and the arithmetic
		// between them is what makes "-10% each time" and "a 30% gold vein" the same statement.
		[Fact]
		public void AFullNode_IsTenSwings()
		{
			Assert.Equal(10u, HarvestCatalog.NodeMaxHealth / HarvestCatalog.SwingCost);
			Assert.Equal(0u, HarvestCatalog.NodeMaxHealth % HarvestCatalog.SwingCost);
		}

		// Not everything destructible is harvestable - the guild-city pieces of #498 are the same IIR with max 1,000,000,
		// and treating one as a node would hand out resources for hitting a wall.
		[Theory]
		[InlineData("Location Object - Walls")]
		[InlineData("Treasure Chest")]
		[InlineData("The Hanging Tree")] // a NAMED landmark, not a Yew/Oak/Ash resource tree
		[InlineData(null)]
		public void NonNodes_AreNotHarvestable(string? name)
		{
			Assert.False(HarvestCatalog.IsHarvestable(name));
			Assert.Null(HarvestCatalog.ResourceFor(name));
		}

		// Every resource the catalog points at must be a real item: a missing Items row cannot be added to a bag, so
		// the harvest would fail at the grant with the node already consumed. A missing HASH used to disqualify an
		// item here too - it no longer does, since the wire token is minted when no capture harvested one (#723).
		[Fact]
		public async Task EveryResource_IsARealGrantableItem()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;
			var broken = new System.Collections.Generic.List<string>();

			foreach (var (node, itemId) in HarvestCatalog.Resources)
			{
				var item = await db.Items.FirstOrDefaultAsync(i => i.Id == itemId, ct);
				if (item == null)
					broken.Add($"{node} -> item {itemId} has no Items row");
			}

			Assert.True(broken.Count == 0, "Harvest resources that cannot be granted: " + string.Join("; ", broken));
		}

		// The catalog is keyed by node NAME, so a node shipping under a name the catalog does not know is simply
		// un-harvestable with nothing to say so. This is the gate that catches that - it reads the SHIPPED rows rather
		// than a list, so a re-import that adds a new node type fails here instead of shipping an inert node.
		//
		// Scoped to the resource-node name SHAPES (Tree/Rock/Node/Vein/Shrub) so it does not drag in landmarks like
		// "The Hanging Tree" or "Two Trees"; those are decor that happens to share a word, and the allowlist below says
		// so explicitly rather than letting the filter quietly skip them.
		[Fact]
		public async Task EveryShippedResourceNode_IsInTheCatalog()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			// Decor whose name contains a resource word but which is NOT a harvest node.
			string[] notResources = ["The Hanging Tree", "Two Trees", "The Rock", "Small Rock", "Moss-Covered Rock"];

			// Real harvest nodes we CANNOT grant from: no item named Elm or Slate exists in faolan.db, so there is
			// nothing to hand the player. Listed rather than silently filtered, and deliberately not guessed at - an
			// invented id would hand out the wrong resource. Delete an entry the moment its item shows up in an import.
			string[] noResourceItemShipped = ["Elm Tree", "Slate Rock"];

			var names = await db.WorldObjects
				.Where(o => o.Name != null && (o.Name.EndsWith(" Tree") || o.Name.EndsWith(" Rock") || o.Name.EndsWith(" Node") || o.Name.EndsWith(" Vein") || o.Name.EndsWith(" Shrub")))
				.Select(o => o.Name!).Distinct().ToListAsync(ct);

			var uncovered = names.Where(n => !notResources.Contains(n)).Where(n => !noResourceItemShipped.Contains(n))
				.Where(n => !HarvestCatalog.IsHarvestable(n)).OrderBy(n => n).ToList();

			Assert.True(uncovered.Count == 0,
				"Shipped resource nodes with no catalog entry (they would be inert): " + string.Join(", ", uncovered));

			// And the gap list may not rot: an entry whose item HAS since been imported should be promoted to a real
			// catalog row rather than left sitting here looking like a permanent limitation.
			var resolvable = new System.Collections.Generic.List<string>();
			foreach (var node in noResourceItemShipped)
			{
				var resource = node.Replace(" Tree", "").Replace(" Rock", "");
				// No `Hash != 0` here: since #723 an item is grantable whether or not a capture harvested its
				// token, so the only question left is whether the resource item EXISTS.
				if (await db.Items.AnyAsync(i => i.Name == resource, ct))
					resolvable.Add($"{node} -> '{resource}'");
			}

			Assert.True(resolvable.Count == 0,
				"These nodes now HAVE a grantable resource item - add them to HarvestCatalog.Resources and drop them from the gap list: " + string.Join(", ", resolvable));
		}
	}
}
