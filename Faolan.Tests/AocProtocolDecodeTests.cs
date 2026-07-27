using System;
using System.IO;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;
using Faolan.Importer.Captures;
using Faolan.Tests.Support;
using ProtoBuf;
using Xunit;

namespace Faolan.Tests
{
	// Proves the protobuf schema reverse-engineered from AgeOfConan.exe (Faolan.Core/Protobuf/aoc-client-protos.proto, generated to
	// Faolan.Core/Protobuf/AocProtocol.g.cs) decodes the REAL captured inventory wire bytes - and that the
	// decoded ProtoAcgItem.key1 (a ProtoDecryptionKey) is exactly the item's Data1..4 quad, i.e. the per-item TEA
	// key that unlocks its RDBH item template. Ties capture -> typed protobuf -> decryption key together.
	public class AocProtocolDecodeTests
	{
		private const uint Hash = 1129664565;
		private const uint SlaveManaclesId = 3361557;
		private const ulong Slot = 18;
		private static readonly string InventoryCapture = TestPaths.Asset("strie-login-inventory.bin");

		[Fact]
		public async Task GeneratedSchema_Decodes_CapturedInventoryEntry_KeysMatchDataQuad()
		{
			if (!File.Exists(InventoryCapture))
				return;

			// 1) Pull the real captured Slave Manacles equipped entry bytes (an InventoryGame list entry).
			var packet = Rpc.Parse(await File.ReadAllBytesAsync(InventoryCapture, TestContext.Current.CancellationToken));
			var body = new FaolanStream(packet.BodyReader().ReadArrayUInt32Length());
			body.ReadUInt32();
			body.ReadUInt32();
			body.ReadUInt32();
			var parts = FullCharSnapshotCodec.ParseInventoryPacketBody(body);
			var equipped = FullCharSnapshotCodec.GetContainer(parts.Root, ContainerKind.Equipped);
			var captured = SnapshotEntryAccessors.EntryBySlot(equipped!, Slot);
			Assert.NotNull(captured);

			// 2) Decode those bytes with the generated, schema-accurate types.
			using var ms = new MemoryStream(captured!.ToProtobuf());
			var entry = Serializer.Deserialize<ProtoInventoryGame.InventoryListEntry>(ms);

			Assert.NotNull(entry.Item);
			var acg = entry.Item.ItemData;
			Assert.NotNull(acg);
			Assert.NotNull(acg.Key1);
			Assert.Equal(SlaveManaclesId, acg.Template1); // the RDB item-template id
			Assert.Equal(Hash, acg.Hash); // tail.f8 = ProtoAcgItem.hash (what we call "hash")

			// 3) The decoded ProtoDecryptionKey must equal the Data1..4 we import from the same capture - proving the
			//    "identity quad" is the TEA key.
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-proto-{Guid.NewGuid():N}.db");
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
				Assert.Equal(item!.Key.Key1, acg.Key1.Key1);
				Assert.Equal(item.Key.Key2, acg.Key1.Key2);
				Assert.Equal(item.Key.Key3, acg.Key1.Key3);
				Assert.Equal(item.Key.Key4, acg.Key1.Key4);
			}
			finally
			{
				TestDb.Delete(dbPath);
			}
		}

		// Proves protobuf-net re-serializes BYTE-IDENTICALLY to the captured wire - both a single inventory entry and
		// the whole 0x737A6DF9 snapshot body (ProtoFullCharacterIIR). This is the guarantee that lets the typed schema
		// replace the hand-rolled SnapshotItemBuilder / FullCharSnapshotCodec without changing any output byte.
		[Fact]
		public async Task GeneratedSchema_RoundTrips_CapturedSnapshot_ByteIdentical()
		{
			if (!File.Exists(InventoryCapture))
				return;

			var packet = Rpc.Parse(await File.ReadAllBytesAsync(InventoryCapture, TestContext.Current.CancellationToken));
			var body = new FaolanStream(packet.BodyReader().ReadArrayUInt32Length());
			body.ReadUInt32();
			body.ReadUInt32();
			body.ReadUInt32();
			var parts = FullCharSnapshotCodec.ParseInventoryPacketBody(body);

			// One inventory entry round-trips exactly.
			var equipped = FullCharSnapshotCodec.GetContainer(parts.Root, ContainerKind.Equipped);
			var entryBytes = SnapshotEntryAccessors.EntryBySlot(equipped!, Slot)!.ToProtobuf();
			var entry = Serializer.Deserialize<ProtoInventoryGame.InventoryListEntry>(new MemoryStream(entryBytes));
			using var em = new MemoryStream();
			Serializer.Serialize(em, entry);
			Assert.Equal(Convert.ToHexString(entryBytes), Convert.ToHexString(em.ToArray()));

			// The whole snapshot body round-trips exactly through ProtoFullCharacterIIR (unknown fields preserved
			// via IExtensible).
			var rootBytes = parts.Root.ToProtobuf();
			var root = Serializer.Deserialize<ProtoFullCharacterIIR>(new MemoryStream(rootBytes));
			using var rm = new MemoryStream();
			Serializer.Serialize(rm, root);
			Assert.Equal(Convert.ToHexString(rootBytes), Convert.ToHexString(rm.ToArray()));
		}

		private static DatabaseContext NewContext(string path)
		{
			var options = TestDb.Options(path);
			return new DatabaseContext(options);
		}
	}
}
