using System;
using System.Linq;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Protobuf;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// "Created by <player>" on a player-crafted item (#727). The ticket predicted the carrier was the ItemOrigin
	// stat (0x554), on the reasoning that the tooltip label lives in the client's tooltip string block and a stat is
	// the only per-instance channel we knew of. The crafting capture refutes that: it is
	// ProtoInventoryGameEntry.creator_name - field 5, a plain Latin1 STRING on the entry itself, no stat involved.
	//
	// Oracle: NecroL80_mage-necromancer_login_gameplay_annotated_5, the one captured craft. Melvin linked the item in
	// /say before (18:45:24) and after (18:46:09) and narrated "done"; the server's DispatchAddItem lands between
	// them at 18:45:30 (frame 17401) and is the only add in all 125 dumps that carries field 5.
	//
	// ⚠️ The chat link's THIRD quad is what made this look like a key problem: pre-craft it reads
	// `b290805c:29e627ca` twice and post-craft it becomes a copy of the identity quad. That is not a creator slot -
	// it is ProtoAcgItem.key3 holding the NullIdentity sentinel (Faolan.Core.Network/Constants/NullIdentity.cs)
	// because the item did not exist yet. On the wire key3 == key2 == key1 on all 175 captured adds, crafted or not,
	// which is what we already send.
	public class ItemCreatorNameTests
	{
		// The captured DispatchAddItem method body: [container 0x68 Bag][char 0x0802e5d4][pos 28][len 0x81][entry].
		private const string CraftedBody = "000000680802e5d40000001c0000008108f89fddceb3ec8aec201204080010111204080810001206081710bcd8361204082e10" +
		                                   "001a530a140de4b8eaf515a728315e1d73ebf3122560dfce2012140de4b8eaf515a728315e1d73ebf3122560dfce201a140d" +
		                                   "e4b8eaf515a728315e1d73ebf3122560dfce2025c36502002dc3650200382845514c58382a064d656c6c696500000000";

		// The bare ProtoInventoryGameEntry (i_cEntry) out of that body. Ends `2a 06 "NecroL80"` - creator_name last,
		// after the item block.
		private const string CraftedEntry = "08f89fddceb3ec8aec201204080010111204080810001206081710bcd8361204082e10001a530a140de4b8eaf515a728315e1d" +
		                                    "73ebf3122560dfce2012140de4b8eaf515a728315e1d73ebf3122560dfce201a140de4b8eaf515a728315e1d73ebf31225" +
		                                    "60dfce2025c36502002dc3650200382845514c58382a064d656c6c6965";

		private const uint CharId = 0x0802e5d4;
		private const uint ItemPos = 28;

		// "Bloodsilver Khopesh" - the crafted weapon. Its level 40 is a free cross-check on #725: the same 40 shows
		// up as the itemref link's fourth path segment.
		private const uint BloodsilverKhopeshId = 157123;
		private const ulong ItemId = 2366689309355364344;
		private const ulong RecordId = 896060;
		private const string Crafter = "NecroL80";

		// The shipped template + the captured per-instance state rebuild the retail add byte for byte, creator line
		// included. Note the crafted add's stat list is its own shape again - 0x00=17, Duration 0, no StackSize,
		// ItemSource 0 - so it is passed explicitly here rather than through LiveAddEntry (that shape is #722's
		// business; this gate is about field 5).
		[Fact]
		public void CraftedAddItem_CarriesCreatorName_MatchesCapture()
		{
			using var ctx = new DatabaseContext(TestDb.ReadOnly(TestPaths.FaolanDb));
			var item = ctx.Items.Find(BloodsilverKhopeshId);
			Assert.NotNull(item);

			var entry = SnapshotItems.InventoryEntry(ItemId, item!, item!.Hash,
			[
				new SnapshotItems.ItemStat(0, 17), new SnapshotItems.ItemStat(8, 0), new SnapshotItems.ItemStat(23, (uint)RecordId),
				new SnapshotItems.ItemStat(46, 0)
			], Crafter);

			Assert.Equal(CraftedEntry, Convert.ToHexStringLower(entry.ToProtobuf()));
			Assert.Equal(CraftedBody, Convert.ToHexStringLower(AddItemCodec.Build(new AddItem(ContainerKind.Bag, CharId, ItemPos, entry))));
		}

		// The field is PRESENT only on player-made items: 619 of the corpus' inventory entries carry one (5 distinct
		// crafters) and every other entry ends at the item block with no `2a` tag at all. So an absent/empty creator
		// must emit nothing rather than an empty string - protobuf-net would happily write `2a 00` and lengthen every
		// entry we send. (ProtoInventoryGameEntry.CreatorName reads back as "" when unset, hence the ShouldSerialize
		// pattern; see protobuf_presence_needs_shouldserialize.)
		[Theory]
		[InlineData(null)]
		[InlineData("")]
		public void NoCrafter_EmitsNoCreatorField(string? creatorName)
		{
			var item = new Item
			{
				Id = BloodsilverKhopeshId,
				Key = new ProtoDecryptionKey { Key1 = 1, Key2 = 2, Key3 = 3, Key4 = 4 },
				Hash = 0x35505543
			};

			var bare = SnapshotItems.LiveAddEntry(ItemId, RecordId, 1, item, item.Hash);
			var named = SnapshotItems.LiveAddEntry(ItemId, RecordId, 1, item, item.Hash, creatorName: creatorName);

			Assert.Equal(Convert.ToHexStringLower(bare.ToProtobuf()), Convert.ToHexStringLower(named.ToProtobuf()));
			Assert.DoesNotContain("2a", Convert.ToHexStringLower(named.ToProtobuf())[^8..]);
		}

		// The snapshot carries it too - 348 of the 619 sightings are in the Bag container, which is exactly what
		// CharacterInventory.CreatorName persists and FullCharacterRoots renders on the next world entry. (The other
		// 271 are in Equipped, which CharacterEquipment cannot store per slot yet - tracked separately.)
		[Fact]
		public void SnapshotListEntry_CarriesCreatorName()
		{
			var item = new Item
			{
				Id = BloodsilverKhopeshId,
				Key = new ProtoDecryptionKey { Key1 = 1, Key2 = 2, Key3 = 3, Key4 = 4 },
				Hash = 0x35505543
			};

			var entry = SnapshotItems.ListEntry(0, ItemId, RecordId, 1, item, item.Hash, creatorName: Crafter).Item;

			Assert.Equal(Crafter, entry.CreatorName);
			Assert.EndsWith(Convert.ToHexStringLower("\x2a\x06NecroL80"u8.ToArray()), Convert.ToHexStringLower(entry.ToProtobuf()));

			// The stat triple is untouched - creator_name rides beside it, not instead of it.
			Assert.Equal([0u, 23u, 26u], entry.StatLists.Select(s => s.Stat));
		}
	}
}
