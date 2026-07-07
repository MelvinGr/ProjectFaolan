using Faolan.Core;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Codecs.GameServer.FullCharSnapshot;
using Faolan.Core.Network.Codecs.GameServer.FullCharSnapshot;
using Faolan.Core.Network.Protobuf;
using Faolan.Importer.Captures;
using Microsoft.EntityFrameworkCore;
using ProtoBuf;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Faolan.Core.Network.FullCharSnapshot;

namespace Faolan.Tests
{
    // Proves the full chain capture -> CaptureItemImporter -> DB Item -> SnapshotItemBuilder -> on-wire bytes:
    // building the "Slave Manacles" entry from the imported Item (id 3361557) reproduces the captured snapshot
    // entry byte for byte. The validated foundation for building inventories from the DB - and the answer to the
    // add/remove stall (the item's identity is a real, captured one).
    public class SnapshotItemBuilderTests
    {
        private const string InventoryCapture = "/home/melvin/Development/C#/Project Faolan/Captures/strie-login-inventory.bin";
        private const uint Hash = 1129664565;
        private const uint SlaveManaclesId = 3361557;
        private const ulong Count = 1;
        private const ulong ItemId = 2366689308620230826;
        private const ulong RecordId = 2693654;

        // Per-instance state read from the captured Slave Manacles entry (these are NOT template data - they are
        // the character's specific item instance: its GUID, inventory record id, stack count, and hash hash).
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
            var parts = FullCharSnapshotReader.ParseInventoryPacketBody(body);
            var equipped = FullCharSnapshotReader.GetContainer(parts.Root, ContainerKind.Equipped);
            var captured = FullCharSnapshotReader.GetEntryBySlot(equipped, Slot);
            Assert.NotNull(captured);
            var expected = captured!.ToProtobuf();

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
                var built = SnapshotItemBuilder.BuildListEntry(Slot, ItemId, RecordId, Count, item, Hash);
                var produced = built.ToProtobuf();

                Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(produced));

                // 4) And via the item's own hash (harvested into Items) - the fully DB-driven path.
                Assert.Equal(Hash, item.Hash);
                Assert.True(SnapshotItemBuilder.TryBuildEntry(Slot, ItemId, RecordId, Count, item, out var auto));
                Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(auto.ToProtobuf()));
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

            var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-swap-{Guid.NewGuid():N}.db");
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
                Assert.NotEqual(0u, item!.Hash);
                var hash = item.Hash;

                var packet = new ConanPacket(await File.ReadAllBytesAsync(InventoryCapture));
                var body = new FaolanStream(packet.Body.ReadArrayUInt32Length());
                body.ReadUInt32();
                body.ReadUInt32();
                body.ReadUInt32();
                var parts = FullCharSnapshotReader.ParseInventoryPacketBody(body);

                var bag = FullCharSnapshotReader.GetContainer(parts.Root, ContainerKind.Bag);
                var before = FullCharSnapshotReader.GetContainerEntries(bag).ToList();
                var original = before[0];
                var slot = FullCharSnapshotReader.GetEntrySlot(original);
                var itemId = FullCharSnapshotReader.GetEntryItemId(original) ?? 0;
                var recordId = FullCharSnapshotReader.GetEntryRecordId(original) ?? 0;
                var count = FullCharSnapshotReader.GetEntryCount(original) ?? 1;

                var built = SnapshotItemBuilder.BuildListEntry(slot, itemId, recordId, count, item, hash);
                bag.Items[bag.Items.IndexOf(original)] = built;

                // Re-serialize the whole root and re-parse, as the live path does.
                using var reparsedStream = new MemoryStream(parts.Root.ToProtobuf(), false);
                var reparsed = Serializer.Deserialize<ProtoFullCharacterIIR>(reparsedStream);
                var bag2 = FullCharSnapshotReader.GetContainer(reparsed, ContainerKind.Bag);
                var after = FullCharSnapshotReader.GetContainerEntries(bag2).ToList();

                Assert.Equal(before.Count, after.Count); // entry count unchanged (in-place replace)
                var first = FullCharSnapshotReader.GetEntryBySlot(bag2, slot);
                Assert.NotNull(first);
                Assert.Equal(SlaveManaclesId, FullCharSnapshotReader.GetEntryTemplateId(first!));
                Assert.Equal(itemId, FullCharSnapshotReader.GetEntryItemId(first!));
                Assert.Equal(recordId, FullCharSnapshotReader.GetEntryRecordId(first!));
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
