using System.Collections.Generic;
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
		// The destructible class every harvest node arrives as (0x6BD56736).
		private const uint DestructibleClass = 0x6BD56736;

		// The trailer word that carries a destructible's MAX health - Kind 1, ordinal 1 of WorldObjectWords.
		private const int MaxHealthOrdinal = 1;

		// The three rows a capture actually measured, each landing beside its own annotation. If the item ids drift,
		// the identification is what broke.
		//
		// ⚠️ The counts here used to be 40 / 60 / 40, read off the 4th u32 of the DispatchDisplayItemLootChatMessage
		// that follows a harvest. That word is the item's LEVEL, not a count - and Items.Level for Granite, Oak and
		// Iron is 40, 60 and 40, which is why the misread looked like three independent measurements. The count is on
		// the ADD, in stat_list 26, and it is 1 on every captured harvest in both mining captures.
		[Theory]
		[InlineData(3237642u, "Granite Rock", 161206u)] // `just mined`
		[InlineData(3237647u, "Oak Tree", 161213u)] // `gethering wood` - the tree the maintainer chopped down
		[InlineData(3237652u, "Iron Node", 161196u)] // `mining iron`
		public void TheMeasuredNodes_YieldOneUnitPerSwing(uint displayId, string name, uint itemId)
		{
			Assert.Equal(name, HarvestCatalog.NameFor(HarvestCatalog.NodeDisplayCategory, displayId));
			Assert.Equal(itemId, HarvestCatalog.ResourceFor(HarvestCatalog.NodeDisplayCategory, displayId));
			Assert.Equal(HarvestCatalog.YieldPerSwing, HarvestCatalog.YieldFor(displayId));
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
			Assert.Equal(161218u, HarvestCatalog.ResourceFor(HarvestCatalog.NodeDisplayCategory, 3237646)); // Yew Tree
			Assert.Equal(HarvestCatalog.DefaultYield, HarvestCatalog.YieldFor(3237646));
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

		// An LDB id is only unique WITHIN its category, so both halves of the display reference are required. The
		// guild-city destructibles next door are category 50050 with ids as low as 73, and the PvP objectives are
		// 55000 - matching on the id alone would eventually hand a resource out for hitting a wall.
		[Theory]
		[InlineData(50050u, 121u)] // "Location Object - Walls" - a guild-city piece
		[InlineData(55000u, 4356532u)] // "Capturable Objective" - a PvP objective, and it DOES carry max health 1000
		[InlineData(null, null)]
		[InlineData(HarvestCatalog.NodeDisplayCategory, 3966659u)] // "Supply Crate" - right category, not a node
		public void NonNodes_AreNotHarvestable(uint? category, uint? displayId)
		{
			Assert.False(HarvestCatalog.IsHarvestable(category, displayId));
			Assert.Null(HarvestCatalog.ResourceFor(category, displayId));
			Assert.Null(HarvestCatalog.NodeFor(category, displayId));
		}

		// Every resource the catalog points at must be a real item: a missing Items row cannot be added to a bag, so
		// the harvest would fail at the grant with the node already consumed. A missing HASH used to disqualify an
		// item here too - it no longer does, since the wire token is minted when no capture harvested one (#723).
		[Fact]
		public async Task EveryResource_IsARealGrantableItem()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;
			var broken = new List<string>();

			foreach (var node in HarvestCatalog.Nodes.Values.Where(n => n.ResourceItemId != null))
			{
				var item = await db.Items.FirstOrDefaultAsync(i => i.Id == node.ResourceItemId, ct);
				if (item == null)
					broken.Add($"{node.Name} -> item {node.ResourceItemId} has no Items row");
			}

			Assert.True(broken.Count == 0, "Harvest resources that cannot be granted: " + string.Join("; ", broken));
		}

		// ⚠️ THE #734 GATE, and the reason this one is keyed on the DISPLAY id.
		//
		// It used to filter shipped rows by NAME suffix (" Tree" / " Rock" / " Node" / " Vein" / " Shrub"), which made
		// it blind to exactly the rows the catalog was blind to: a silver vein whose name string says "Martial Ring",
		// a copper node called "Vitalic Bronze-studded Armbands", an ash tree called "Bloodsilver-studded Gloves", a
		// cotton shrub called "Blacksmithing Hammer TESTITEM" and three raw NPC_Global_leather_… asset names. Every one
		// of those is a real, streamed, inert node, and the old gate passed with all seven uncovered.
		//
		// The discriminator here is structural instead: a DESTRUCTIBLE whose max-health trailer word is exactly
		// NodeMaxHealth, in the node display category. That is the client's own definition of a harvest node, so a
		// re-import that adds a node type fails here rather than shipping something inert.
		[Fact]
		public async Task EveryShippedResourceNode_IsInTheCatalog()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var shipped = await db.WorldObjects
				.Where(o => o.SubOpcode == DestructibleClass && o.DisplayCategory == HarvestCatalog.NodeDisplayCategory)
				.Where(o => o.Words.Any(w => w.Ordinal == MaxHealthOrdinal && w.Value == HarvestCatalog.NodeMaxHealth))
				.Select(o => o.DisplayId).Distinct().ToListAsync(ct);

			Assert.NotEmpty(shipped); // the gate must not go vacuous if the class word or the trailer layout moves

			var uncovered = shipped.Where(id => !HarvestCatalog.Nodes.ContainsKey(id!.Value)).OrderBy(id => id).ToList();
			Assert.True(uncovered.Count == 0,
				"Shipped harvest nodes with no catalog entry (they would be inert): " + string.Join(", ", uncovered));
		}

		// The catalog's name for a node has to be the name the client shows, and the shipped rows are the only check
		// available without the client LDB: retail's own name string is right on MOST rows of a node type and wrong on
		// a few, so the catalog name must appear among them. A typo, or a name copied off one of the WRONG rows, fails
		// here.
		[Fact]
		public async Task EveryCatalogName_AppearsOnAShippedRowOfThatNode()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var namesById = (await db.WorldObjects
					.Where(o => o.SubOpcode == DestructibleClass && o.DisplayCategory == HarvestCatalog.NodeDisplayCategory && o.Name != null)
					.Select(o => new { o.DisplayId, o.Name }).Distinct().ToListAsync(ct))
				.GroupBy(r => r.DisplayId!.Value)
				.ToDictionary(g => g.Key, g => g.Select(r => r.Name!).ToArray());

			var wrong = HarvestCatalog.Nodes.Values
				.Where(n => namesById.TryGetValue(n.DisplayId, out var names) && !names.Contains(n.Name))
				.Select(n => $"{n.DisplayId} '{n.Name}' - shipped rows say {string.Join(" / ", namesById[n.DisplayId])}")
				.ToList();

			Assert.True(wrong.Count == 0, "Catalog names that no shipped row of that node carries:\n  " + string.Join("\n  ", wrong));
		}

		// The content gap may not rot: a node listed with no resource item whose item HAS since been imported should be
		// promoted to a real payout rather than left inert. This is the old "gap list" check, moved off a test-local
		// string array and onto the catalog's own null entries.
		[Fact]
		public async Task NodesWithNoResource_StillHaveNoMatchingItem()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;
			var resolvable = new List<string>();

			foreach (var node in HarvestCatalog.Nodes.Values.Where(n => n.ResourceItemId == null))
			{
				// "Nickel Deposit" -> "Nickel", "Elm Tree" -> "Elm", "Marble" -> "Marble".
				var resource = node.Name.Split(' ')[0];
				if (await db.Items.AnyAsync(i => i.Name == resource, ct))
					resolvable.Add($"{node.Name} -> '{resource}'");
			}

			Assert.True(resolvable.Count == 0,
				"These nodes now HAVE a grantable resource item - give them a ResourceItemId instead of null: " + string.Join(", ", resolvable));
		}
	}
}
