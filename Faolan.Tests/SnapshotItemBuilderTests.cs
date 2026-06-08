using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Data;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;
using Faolan.GameServer.Packets._0x737A6DF9_Test;
using Faolan.Importer;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// Proves the full chain capture -> CaptureItemImporter -> DB Item -> SnapshotItemBuilder -> on-wire bytes:
	// building the "Slave Manacles" entry from the imported Item (id 3361557) reproduces the captured snapshot
	// entry byte for byte. The validated foundation for building inventories from the DB - and the answer to the
	// add/remove stall (the item's identity is a real, captured one).
	public class SnapshotItemBuilderTests
	{
		private const string InventoryCapture = "/home/melvin/Development/C#/Project Faolan/Captures/strie-login-inventory.bin";
		private const uint Archetype = 1129664565;
		private const uint SlaveManaclesId = 3361557;
		private const ulong Count = 1;
		private const ulong ItemId = 2366689308620230826;
		private const ulong RecordId = 2693654;

		// Per-instance state read from the captured Slave Manacles entry (these are NOT template data - they are
		// the character's specific item instance: its GUID, inventory record id, stack count, and archetype hash).
		private const ulong Slot = 18;

		[Fact]
		public async Task BuildEntry_FromImportedItem_MatchesCapturedSlaveManacles()
		{
			if (!File.Exists(InventoryCapture))
				return;

			// 1) Extract the captured Slave Manacles entry bytes.
			var packet = new ConanPacket(await File.ReadAllBytesAsync(InventoryCapture));
			var body = new FaolanStream(packet.Body.ReadArrayUInt32Length());
			body.ReadUInt32();
			body.ReadUInt32();
			body.ReadUInt32();
			var parts = InventorySnapshotInspector.ParseInventoryPacketBody(body);
			var equipped = InventorySnapshotInspector.GetContainer(parts.Root, InventorySnapshotInspector.ContainerKind.Equipped);
			var captured = InventorySnapshotInspector.GetEntryBySlot(equipped, Slot);
			Assert.NotNull(captured);
			var expected = captured!.MessageValue!.ToArray();

			// 2) Import the items from this capture into a temp DB and load the Slave Manacles template.
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-snap-{Guid.NewGuid():N}.db");
			try
			{
				Item item;
				await using (var db = NewContext(dbPath))
				{
					await db.Database.EnsureCreatedAsync();
					await CaptureItemImporter.ImportAsync(db, [await File.ReadAllBytesAsync(InventoryCapture)]);
					item = await db.Items.FindAsync(SlaveManaclesId);
				}

				Assert.NotNull(item);

				// 3) Build the entry from the DB template + captured per-instance state, assert byte-identical.
				var built = SnapshotItemBuilder.BuildEntry(Slot, ItemId, RecordId, Count, item, Archetype);
				var produced = built.MessageValue!.ToArray();

				Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(produced));

				// 4) And via the item's own archetype (harvested into Items) - the fully DB-driven path.
				Assert.Equal(Archetype, item.Archetype);
				Assert.True(SnapshotItemBuilder.TryBuildEntry(Slot, ItemId, RecordId, Count, item, out var auto));
				Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(auto.MessageValue!.ToArray()));
			}
			finally
			{
				if (File.Exists(dbPath))
					File.Delete(dbPath);
			}
		}

		// Mirrors RetailLoginReplay.ApplyDbInventory: swap the bag's first entry in place with a DB-built entry
		// keeping slot/itemId/recordId/count, re-serialize, re-parse, and assert the bag entry count is unchanged
		// and the first bag item now reports the DB template - proving the swap is structurally sound.
		[Fact]
		public async Task ReplaceFirstBagItem_InPlace_KeepsCountAndSwapsTemplate()
		{
			if (!File.Exists(InventoryCapture))
				return;

			// Swap in Slave Manacles (3361557) - a buildable item present in this capture.
			const uint swapInItem = SlaveManaclesId;

			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-swap-{Guid.NewGuid():N}.db");
			try
			{
				Item item;
				await using (var db = NewContext(dbPath))
				{
					await db.Database.EnsureCreatedAsync();
					await CaptureItemImporter.ImportAsync(db, [await File.ReadAllBytesAsync(InventoryCapture)]);
					item = await db.Items.FindAsync(swapInItem);
				}

				Assert.NotNull(item);
				Assert.NotEqual(0u, item!.Archetype);
				var archetype = item.Archetype;

				var packet = new ConanPacket(await File.ReadAllBytesAsync(InventoryCapture));
				var body = new FaolanStream(packet.Body.ReadArrayUInt32Length());
				body.ReadUInt32();
				body.ReadUInt32();
				body.ReadUInt32();
				var parts = InventorySnapshotInspector.ParseInventoryPacketBody(body);

				var bag = InventorySnapshotInspector.GetContainer(parts.Root, InventorySnapshotInspector.ContainerKind.Bag);
				var before = InventorySnapshotInspector.GetContainerEntries(bag).ToList();
				var original = before[0];
				var slot = InventorySnapshotInspector.GetEntrySlot(original) ?? 0;
				var itemId = InventorySnapshotInspector.GetEntryItemId(original) ?? 0;
				var recordId = InventorySnapshotInspector.GetEntryRecordId(original) ?? 0;
				var count = InventorySnapshotInspector.GetEntryCount(original) ?? 1;

				var built = SnapshotItemBuilder.BuildEntry(slot, itemId, recordId, count, item, archetype);
				bag.Fields[bag.Fields.IndexOf(original)] = built;

				// Re-serialize the whole root and re-parse, as the live path does.
				var reparsed = ProtoMessage.Parse(parts.Root.ToArray());
				var bag2 = InventorySnapshotInspector.GetContainer(reparsed, InventorySnapshotInspector.ContainerKind.Bag);
				var after = InventorySnapshotInspector.GetContainerEntries(bag2).ToList();

				Assert.Equal(before.Count, after.Count); // entry count unchanged (in-place replace)
				var first = InventorySnapshotInspector.GetEntryBySlot(bag2, slot);
				Assert.NotNull(first);
				Assert.Equal(swapInItem, InventorySnapshotInspector.GetEntryTemplateId(first!));
				Assert.Equal(itemId, InventorySnapshotInspector.GetEntryItemId(first!));
				Assert.Equal(recordId, InventorySnapshotInspector.GetEntryRecordId(first!));
			}
			finally
			{
				if (File.Exists(dbPath))
					File.Delete(dbPath);
			}
		}

		private static DatabaseContext NewContext(string path)
		{
			var options = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={path}", x => x.MigrationsAssembly("Faolan")).Options;
			return new DatabaseContext(options);
		}
	}
}
