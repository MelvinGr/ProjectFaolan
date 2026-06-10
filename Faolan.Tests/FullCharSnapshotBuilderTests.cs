using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.Packets._0x737A6DF9_Test;
using Xunit;

namespace Faolan.Tests
{
	// Locks the generated FullChar containers to the captured shape: every captured snapshot carries exactly
	// 4 containers in kind order 101/104/112/113 with max_size 1028/24/48/48 and disabled_slots_count
	// 0/0/12/0, owner = the client instance; entry ids follow the retail scheme (0x20D82B63 epoch high32)
	// and are unique per item.
	public class FullCharSnapshotBuilderTests
	{
		private static readonly Item Manacles = new Item { Id = 3361557, Data1 = 1, Data2 = 2, Data3 = 3, Data4 = 4, Archetype = 0x43555035 };
		private static readonly Item Signet = new Item { Id = 3774542, Data1 = 5, Data2 = 6, Data3 = 7, Data4 = 8, Archetype = 0x12345678 };

		[Fact]
		public void BuildContainers_MatchesTheCapturedShape()
		{
			var equipped = new Dictionary<ulong, (Item, uint)> { [0x12] = (Manacles, Manacles.Archetype) };

			var containers = FullCharSnapshotBuilder.BuildContainers(0x02000005, equipped, (Signet, Signet.Archetype));

			Assert.Equal(new uint[] { 101, 104, 112, 113 }, containers.Select(c => c.InventoryId.Type));
			Assert.All(containers, c => Assert.Equal(0x02000005u, c.InventoryId.Instance));
			Assert.Equal(new uint[] { 1028, 24, 48, 48 }, containers.Select(c => c.MaxSize));
			Assert.Equal(new uint[] { 0, 0, 12, 0 }, containers.Select(c => c.DisabledSlotsCount));

			var worn = Assert.Single(containers[0].Items);
			Assert.Equal(0x12u, worn.Pos);
			Assert.Equal(3361557u, worn.Item.ItemData.Template1);
			Assert.Equal(0x43555035u, worn.Item.ItemData.Hash);
			Assert.Equal(0x20D82B63u, (uint)(worn.Item.ItemId >> 32)); // retail itemId epoch

			var bag = Assert.Single(containers[1].Items);
			Assert.Equal(1u, bag.Pos); // the captured fresh-bag first cell
			Assert.Equal(3774542u, bag.Item.ItemData.Template1);

			Assert.Empty(containers[2].Items);
			Assert.Empty(containers[3].Items);
		}

		[Fact]
		public void BuildContainers_AllocatesUniqueStableIds()
		{
			var equipped = new Dictionary<ulong, (Item, uint)>
			{
				[0x05] = (Manacles, Manacles.Archetype),
				[0x12] = (Manacles, Manacles.Archetype)
			};

			var first = FullCharSnapshotBuilder.BuildContainers(0x02000005, equipped, (Signet, Signet.Archetype));
			var second = FullCharSnapshotBuilder.BuildContainers(0x02000005, equipped, (Signet, Signet.Archetype));

			var ids = first.SelectMany(c => c.Items).Select(e => e.Item.ItemId).ToList();
			var recordIds = first.SelectMany(c => c.Items).SelectMany(e => e.Item.StatLists.Where(s => s.Stat == 23)).Select(s => s.Value).ToList();

			Assert.Equal(ids.Count, ids.Distinct().Count());
			Assert.Equal(recordIds.Count, recordIds.Distinct().Count());
			Assert.Equal(ids, second.SelectMany(c => c.Items).Select(e => e.Item.ItemId).ToList()); // stable across logins
		}
	}
}
