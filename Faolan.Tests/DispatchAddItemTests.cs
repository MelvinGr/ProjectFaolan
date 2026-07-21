using System;
using System.IO;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.GameServer;
using Faolan.Importer.Captures;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Golden-byte test for GameCharInterface.DispatchAddItem (s2c 0x2020) - the incremental, mid-session item
	// grant that [[protocol_containeradditem]] wrongly believed didn't exist. Decoded from the strickenbla
	// questcomplete capture: the very first add was a "Slave Manacles" (template 3361557, the same item the
	// snapshot test builds), pushed into the backpack (container 0x68) at slot 0. We rebuild it from the DB
	// template + the captured per-instance state and assert both the entry blob and the full method body match
	// the capture byte for byte.
	public class DispatchAddItemTests
	{
		// Captured DispatchAddItem method body (frame 2881 of the questcomplete capture).
		private const string CapturedBody = "0000006801316d8a000000000000007d08b2b6ecdab1ec8aec201204080010011208080810ffffffff0f1206081710b2e634" +
		                                    "1204082e10071a530a140d99bf60dd15f67309761dcaed497925d2c7ed8d12140d99bf60dd15f67309761dcaed497925d2c" +
		                                    "7ed8d1a140d99bf60dd15f67309761dcaed497925d2c7ed8d25154b33002d154b33003801453550554300000000";

		// The bare ProtoInventoryGameEntry carried as i_cEntry (the InventoryListEntry.item without the Pos wrapper).
		private const string CapturedEntry = "08b2b6ecdab1ec8aec201204080010011208080810ffffffff0f1206081710b2e6341204082e10071a530a140d99bf60dd15" +
		                                     "f67309761dcaed497925d2c7ed8d12140d99bf60dd15f67309761dcaed497925d2c7ed8d1a140d99bf60dd15f67309761dca" +
		                                     "ed497925d2c7ed8d25154b33002d154b330038014535505543";

		private const uint CharId = 0x01316d8a;
		private const uint ItemPos = 0;
		private const uint SlaveManaclesId = 3361557;

		// Per-instance state read from the captured add-item: the item GUID, inventory record id, container slot,
		// and the four extra stat records the live add carries (flags, stat8, recordId, level).
		private const ulong ItemId = 2366689308843907890;
		private const ulong RecordId = 865074;

		private static readonly string InventoryCapture = TestPaths.Asset("strie-login-inventory.bin");

		[Fact]
		public async Task AddItemBody_FromImportedItem_MatchesCapturedSlaveManacles()
		{
			if (!File.Exists(InventoryCapture))
				return;

			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-additem-{Guid.NewGuid():N}.db");
			try
			{
				Item item;
				await using (var db = NewContext(dbPath))
				{
					await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
					await CaptureItemImporter.ImportAsync(db, [await File.ReadAllBytesAsync(InventoryCapture, TestContext.Current.CancellationToken)]);
					item = (await db.Items.FindAsync([SlaveManaclesId], TestContext.Current.CancellationToken))!;
				}

				Assert.NotNull(item);
				Assert.NotEqual(0u, item!.Hash);

				// 1) The entry blob: same template the snapshot builds, but the live add's per-instance stat set.
				var entry = SnapshotItemBuilder.BuildInventoryEntry(ItemId, item, item.Hash,
				[
					new SnapshotItemBuilder.ItemStat(0, 1), new SnapshotItemBuilder.ItemStat(8, 0xffffffff), new SnapshotItemBuilder.ItemStat(23, (uint)RecordId),
					new SnapshotItemBuilder.ItemStat(46, 7)
				]);
				var entryBytes = AddItemBody.SerializeInventoryEntry(entry);
				Assert.Equal(CapturedEntry, Convert.ToHexStringLower(entryBytes));

				// 2) The full method body framing around that entry.
				var s = new FaolanStream();
				AddItemBody.WriteAddItemBody(s, GameServerListener.InventoryContainerBackpack, CharId, ItemPos, entryBytes);
				Assert.Equal(CapturedBody, Convert.ToHexStringLower(s.ToArray()));
			}
			finally
			{
				if (File.Exists(dbPath))
					File.Delete(dbPath);
			}
		}

		private static DatabaseContext NewContext(string path)
		{
			var options = TestDb.Options(path);
			return new DatabaseContext(options);
		}
	}
}
