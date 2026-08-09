using System.Collections.Generic;
using System.IO;
using System.Linq;
using Faolan.Core;
using Faolan.Core.Crypt;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Locks the generated FullChar containers to the captured shape: every captured snapshot carries exactly
	// 4 containers in kind order 101/104/112/113 with max_size 1028/24/48/48 and disabled_slots_count
	// 0/0/12/0, owner = the client instance; entry ids follow the retail scheme (0x20D82B63 epoch high32)
	// and are unique per item.
	public class FullCharacterRootsTests
	{
		private static readonly Item Manacles = new Item
		{
			Id = 3361557,
			Key = new ProtoDecryptionKey
			{
				Key1 = 1,
				Key2 = 2,
				Key3 = 3,
				Key4 = 4
			},
			Hash = 0x43555035
		};

		private static readonly Item Signet = new Item
		{
			Id = 3774542,
			Key = new ProtoDecryptionKey
			{
				Key1 = 5,
				Key2 = 6,
				Key3 = 7,
				Key4 = 8
			},
			Hash = 0x12345678
		};

		// Entry ids are the ITEM's own identity now (#750), so uniqueness and stability are properties of the row
		// rather than of a formula: two copies of one template in two slots get different ids because they are
		// different items, and re-rendering the same items twice gives the same ids because it is the same items.
		[Fact]
		public void BuildContainers_AllocatesUniqueStableIds()
		{
			var held = new[]
			{
				Worn(11, CharacterEquipmentSlot.Chest, Manacles), Worn(12, CharacterEquipmentSlot.Wrist, Manacles),
				InBag(13, 1, Signet)
			};

			var first = FullCharacterRoots.Containers(0x02000005, held);
			var second = FullCharacterRoots.Containers(0x02000005, held);

			var ids = first.SelectMany(c => c.Items).Select(e => e.Item.ItemId).ToList();
			var recordIds = first.SelectMany(c => c.Items).SelectMany(e => e.Item.StatLists.Where(s => s.Stat == 23)).Select(s => s.Value).ToList();

			Assert.Equal(ids.Count, ids.Distinct().Count());
			Assert.Equal(recordIds.Count, recordIds.Distinct().Count());
			Assert.Equal(ids, second.SelectMany(c => c.Items).Select(e => e.Item.ItemId).ToList()); // stable across logins
			// The low 32 bits ARE the instance id, under the retail epoch.
			Assert.Equal(new ulong[] { 11, 12, 13 }, ids.Order().Select(id => id & 0xFFFFFFFF));
		}

		// The equipped bag/resource-bag grow their containers: the backpack (kind 104) by Item.BagExtraSlots and the
		// resource container (kind 112) by Item.ResourceBagExtraSlots, the same base+extra sizing (#12).
		[Fact]
		public void BuildContainers_EquippedBagsGrowTheirContainers()
		{
			var backpackBag = new Item
			{
				Id = 5061820,
				Key = new ProtoDecryptionKey
				{
					Key1 = 9
				},
				Hash = 0x35555043,
				BagExtraSlots = 216
			};
			var resourceBag = new Item
			{
				Id = 5061819,
				Key = new ProtoDecryptionKey
				{
					Key1 = 10
				},
				Hash = 0x46555044,
				ResourceBagExtraSlots = 102
			};

			var containers = FullCharacterRoots.Containers(0x02000005, [
				Worn(1, CharacterEquipmentSlot.Bag, backpackBag), // slot 3
				Worn(2, CharacterEquipmentSlot.ResourceBag, resourceBag) // slot 26
			]);

			// backpack 24+216=240, resource 48+102=150; equipped/quest unchanged; disabled_slots unchanged.
			Assert.Equal(new uint[] { 1028, 240, 150, 48 }, containers.Select(c => c.MaxSize));
			Assert.Equal(new uint[] { 0, 0, 12, 0 }, containers.Select(c => c.DisabledSlotsCount));

			// both bags render as worn items in the equipped container at their slots.
			Assert.Contains(containers[0].Items, e => e.Pos == (ulong)CharacterEquipmentSlot.Bag);
			Assert.Contains(containers[0].Items, e => e.Pos == (ulong)CharacterEquipmentSlot.ResourceBag);
		}

		[Fact]
		public void BuildContainers_MatchesTheCapturedShape()
		{
			var containers = FullCharacterRoots.Containers(0x02000005, [Worn(1, CharacterEquipmentSlot.Wrist, Manacles), InBag(2, 1, Signet)]);

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

		// Bag items render at their cells, pos-ascending, whatever order the rows arrive in.
		//
		// This used to also cover the legacy FirstPosBag - an item that rendered at bag cell 1 with no row behind it, so
		// a real row at cell 1 had to WIN over it and the two sources could otherwise claim the same cell. #750 removed
		// that second source: the starter item is an ordinary bag item, and a cell has exactly one thing in it (the
		// UNIQUE index on (owner, container, slot) is what makes that structural rather than a rule to remember).
		[Fact]
		public void BuildContainers_RendersBagItems()
		{
			var held = new[]
			{
				InBag(1, 3, Manacles, count: 2),
				InBag(2, 1, Signet),
				// No HARVESTED token - renders on a minted one (#723).
				InBag(3, 5, new Item
				{
					Id = 9,
					Hash = 0
				})
			};

			var bag = FullCharacterRoots.Containers(0x02000005, held)[1].Items;

			Assert.Equal(new uint[] { 1, 3, 5 }, bag.Select(e => e.Pos)); // pos-ascending
			Assert.Equal(Signet.Id, bag[0].Item.ItemData.Template1);
			Assert.Equal(Manacles.Id, bag[1].Item.ItemData.Template1);
			Assert.Equal(2u, bag[1].Item.StatLists.Single(s => s.Stat == 26).Value); // stack count from the item

			// The hash-less item used to be dropped here, which is what made a claim/AH item vanish on relog (#719).
			Assert.Equal(AcgHash.Mint(9), bag[2].Item.ItemData.Hash);
		}

		// A GEMMED item restates its full gem list in slot_map, which is how it survives a relog (#750). Populated
		// NOWHERE before, so every socketed item silently came back gemless.
		[Fact]
		public void BuildContainers_RestatesSocketedGems()
		{
			var gem = new Item
			{
				Id = 3976768,
				Name = "Cabochon Azurite",
				Key = new ProtoDecryptionKey
				{
					Key1 = 0x7608E506
				},
				Level = 80
			};
			var gemmed = InBag(1, 4, Manacles);
			gemmed.Sockets.Add(new ItemInstanceSocket
			{
				SocketIndex = 2,
				GemItemId = gem.Id,
				Gem = gem
			});
			gemmed.Sockets.Add(new ItemInstanceSocket
			{
				SocketIndex = 0,
				GemItemId = gem.Id,
				Gem = gem
			});

			var entry = Assert.Single(FullCharacterRoots.Containers(0x02000005, [gemmed])[1].Items);

			// Ascending by socket index, whatever order they were added in, and each names the GEM - not the item.
			Assert.Equal(new uint[] { 0, 2 }, entry.Item.SlotMaps.Select(m => m.Slot));
			Assert.All(entry.Item.SlotMaps, m => Assert.Equal(gem.Id, m.Item.Template1));
			// The gem's own level, and a MINTED token (the corpus carries hash 0 on every captured gem block).
			Assert.All(entry.Item.SlotMaps, m => Assert.Equal(80u, m.Item.Level));
			Assert.All(entry.Item.SlotMaps, m => Assert.Equal(AcgHash.Mint(gem.Id), m.Item.Hash));
		}

		private static ItemInstance InBag(uint id, int slot, Item template, uint count = 1)
		{
			return new ItemInstance
			{
				Id = id,
				Container = ContainerKind.Bag,
				Slot = slot,
				TemplateId = template.Id,
				Template = template,
				Count = count
			};
		}

		private static ItemInstance Worn(uint id, CharacterEquipmentSlot slot, Item template)
		{
			return new ItemInstance
			{
				Id = id,
				Container = ContainerKind.Equipped,
				Slot = (int)slot,
				TemplateId = template.Id,
				Template = template
			};
		}

		// The world-entry tail patch (live level + current vitals) must survive the tails serialize and leave
		// everything else byte-stable: re-serializing with the SAME values is a no-op, and a changed level
		// reads back changed.
		[Fact]
		public void TailPatch_RoundTripsThroughTheTailsModel()
		{
			var bigboy = TestPaths.Asset("account-stricken997-character-Strie-login-randomActions-bigboy.bin");
			if (!File.Exists(bigboy))
				return;

			var packet = Rpc.Parse(File.ReadAllBytes(bigboy));
			var body = new FaolanStream(packet.BodyReader().ReadArrayUInt32Length());
			body.ReadUInt32();
			body.ReadUInt32();
			body.ReadUInt32();
			var parts = SnapshotPackets.Parse(body);

			var original = FullCharSnapshotSubClassCodec.Build(parts.SubClass);

			// The lead quest's LEVEL word (@+0x10) is a modelled field on the journal record, so writing it is an
			// ordinary value edit rather than a byte patch - and it stays fixed-width.
			parts.SubClass.Quests.Records[0].Level = 42;
			var patched = FullCharSnapshotSubClassCodec.Parse(FullCharSnapshotSubClassCodec.Build(parts.SubClass));
			Assert.Equal(42u, patched.Quests.Records[0].Level);
			Assert.Equal(original.Length, FullCharSnapshotSubClassCodec.Build(parts.SubClass).Length);

			// The live-vitals patch slots round-trip too. The bigboy capture is the tails model's reference
			// fixture, so its stat table MUST be recognized - a silent skip here would mean the world-entry
			// patch never fires. (MaxExp is read-only: it is authored by stat id on the SHEET, not positionally - #447.)
			Assert.True(parts.SubClass.HasKnownRepeatedStatTable);
			{
				var maxExp = parts.SubClass.PostAccessBlock!.MaxExp;
				parts.SubClass.PostAccessBlock.CurrentHp = 1234;
				var statPatched = FullCharSnapshotSubClassCodec.Parse(FullCharSnapshotSubClassCodec.Build(parts.SubClass));
				Assert.Equal(1234u, statPatched.PostAccessBlock!.CurrentHp);
				Assert.Equal(maxExp, statPatched.PostAccessBlock.MaxExp);
			}
		}
	}
}
