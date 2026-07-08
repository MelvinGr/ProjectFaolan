using System.Collections.Generic;
using System.IO;
using System.Linq;
using Faolan.Core;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Packets.Conan;
using Xunit;

namespace Faolan.Tests
{
	// Locks the generated FullChar containers to the captured shape: every captured snapshot carries exactly
	// 4 containers in kind order 101/104/112/113 with max_size 1028/24/48/48 and disabled_slots_count
	// 0/0/12/0, owner = the client instance; entry ids follow the retail scheme (0x20D82B63 epoch high32)
	// and are unique per item.
	public class FullCharSnapshotBuilderTests
	{
		private static readonly Item Manacles = new Item
		{
			Id = 3361557,
			Data1 = 1,
			Data2 = 2,
			Data3 = 3,
			Data4 = 4,
			Hash = 0x43555035
		};

		private static readonly Item Signet = new Item
		{
			Id = 3774542,
			Data1 = 5,
			Data2 = 6,
			Data3 = 7,
			Data4 = 8,
			Hash = 0x12345678
		};

		[Fact]
		public void BuildContainers_AllocatesUniqueStableIds()
		{
			var equipped = new Dictionary<ulong, (Item, uint)>
			{
				[0x05] = (Manacles, Manacles.Hash),
				[0x12] = (Manacles, Manacles.Hash)
			};

			var first = FullCharSnapshotBuilder.BuildContainers(0x02000005, equipped, (Signet, Signet.Hash));
			var second = FullCharSnapshotBuilder.BuildContainers(0x02000005, equipped, (Signet, Signet.Hash));

			var ids = first.SelectMany(c => c.Items).Select(e => e.Item.ItemId).ToList();
			var recordIds = first.SelectMany(c => c.Items).SelectMany(e => e.Item.StatLists.Where(s => s.Stat == 23)).Select(s => s.Value).ToList();

			Assert.Equal(ids.Count, ids.Distinct().Count());
			Assert.Equal(recordIds.Count, recordIds.Distinct().Count());
			Assert.Equal(ids, second.SelectMany(c => c.Items).Select(e => e.Item.ItemId).ToList()); // stable across logins
		}

		[Fact]
		public void BuildContainers_MatchesTheCapturedShape()
		{
			var equipped = new Dictionary<ulong, (Item, uint)> { [0x12] = (Manacles, Manacles.Hash) };

			var containers = FullCharSnapshotBuilder.BuildContainers(0x02000005, equipped, (Signet, Signet.Hash));

			Assert.Equal(new uint[]
				{
					101,
					104,
					112,
					113
				},
				containers.Select(c => c.InventoryId.Type));
			Assert.All(containers, c => Assert.Equal(0x02000005u, c.InventoryId.Instance));
			Assert.Equal(new uint[]
				{
					1028,
					24,
					48,
					48
				},
				containers.Select(c => c.MaxSize));
			Assert.Equal(new uint[]
				{
					0,
					0,
					12,
					0
				},
				containers.Select(c => c.DisabledSlotsCount));

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

		// Persisted CharacterInventory rows fill their container by kind: bag rows render at their cells (the
		// seeded FirstPosBag yields its cell to a persisted row), entries stay pos-ascending, and rows without
		// buildable identity are absent.
		[Fact]
		public void BuildContainers_RendersPersistedBagRows()
		{
			var rows = new[]
			{
				new CharacterInventory
				{
					Container = 104,
					Slot = 3,
					Item = Manacles,
					ItemId = Manacles.Id,
					Count = 2
				},
				new CharacterInventory
				{
					Container = 104,
					Slot = 1,
					Item = Signet,
					ItemId = Signet.Id
				}, // occupies the FirstPosBag cell
				new CharacterInventory
				{
					Container = 104,
					Slot = 5,
					Item = new Item
					{
						Id = 9,
						Hash = 0
					},
					ItemId = 9
				} // not buildable
			};

			var containers = FullCharSnapshotBuilder.BuildContainers(0x02000005, new Dictionary<ulong, (Item, uint)>(), (Manacles, Manacles.Hash), rows);

			var bag = containers[1].Items;
			Assert.Equal(new uint[]
				{
					1,
					3
				},
				bag.Select(e => e.Pos)); // row at 1 wins over FirstPosBag; pos-ascending
			Assert.Equal(Signet.Id, bag[0].Item.ItemData.Template1);
			Assert.Equal(Manacles.Id, bag[1].Item.ItemData.Template1);
			Assert.Equal(2u, bag[1].Item.StatLists.Single(s => s.Stat == 26).Value); // stack count from the row
		}

		// The world-entry tail patch (live level + current vitals) must survive the tails serialize and leave
		// everything else byte-stable: re-serializing with the SAME values is a no-op, and a changed level
		// reads back changed.
		[Fact]
		public void TailPatch_RoundTripsThroughTheTailsModel()
		{
			const string bigboy = "/home/melvin/Development/C#/Project Faolan/Captures/account-stricken997-character-Strie-login-randomActions-bigboy.bin";
			if (!File.Exists(bigboy))
				return;

			var packet = new ConanPacket(File.ReadAllBytes(bigboy));
			var body = new FaolanStream(packet.Body.ReadArrayUInt32Length());
			body.ReadUInt32();
			body.ReadUInt32();
			body.ReadUInt32();
			var parts = FullCharSnapshotReader.ParseInventoryPacketBody(body);

			var original = parts.SubClass.ToArray();
			parts.SubClass.Level = parts.SubClass.Level; // same-value writes are a no-op
			Assert.Equal(original, parts.SubClass.ToArray());

			parts.SubClass.Level = 42;
			var patched = FullCharSnapshotSubClass.Deserialize(parts.SubClass.ToArray());
			Assert.Equal(42u, patched.Level);
			Assert.Equal(original.Length, parts.SubClass.ToArray().Length); // fixed-width patch, no resize

			// The live-vitals/XP patch slots round-trip too. The bigboy capture is the tails model's reference
			// fixture, so its stat table MUST be recognized - a silent skip here would mean the world-entry
			// patch never fires.
			Assert.True(parts.SubClass.HasKnownRepeatedStatTable);
			{
				parts.SubClass.PostAccessBlock!.CurrentHp = 1234;
				parts.SubClass.PostAccessBlock.CurrentXp = 5678;
				var statPatched = FullCharSnapshotSubClass.Deserialize(parts.SubClass.ToArray());
				Assert.Equal(1234u, statPatched.PostAccessBlock!.CurrentHp);
				Assert.Equal(5678u, statPatched.PostAccessBlock.CurrentXp);
			}
		}
	}
}
