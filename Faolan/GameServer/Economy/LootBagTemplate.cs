using Faolan.Core;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Protobuf;
using Faolan.GameServer.Spawn;

namespace Faolan.GameServer.Economy
{
	// The canonical "Bag of loot" definition, built ENTIRELY FROM SCRATCH (#193): the identity columns + the decoded
	// kd-tree bounds (typed, BuildBounds) + a clean ProtoLootBagIIR scaffold (LootBagMessageCodec). No embedded asset
	// and no baked capture state - the former LootBagKdTreeTail.bin carried the captured bag's stale OwnerId + a
	// captured item, which SpliceLootContents only partly overwrote (it sets cash + items, leaving owner/inventory-id
	// baked); this constructs both halves so a spawned bag carries no session leftovers. WorldObjectBuilder.BuildFromScratch
	// assembles header/transform + identity-from-columns + this tail; SpliceLootContents then splices the live items + cash.
	public static class LootBagTemplate
	{
		public const uint SubOpcode = 0xF096251E; // LootBagFullUpdateIIR
		public const uint TypeTag = 0x0000C87A; // the loot typetag (WorldLoot.LootTypeTag)
		private const uint DefaultMaxSize = 10;

		// The loot inventory's container KIND (ID32 type) on the wire; the instance is left 0 here and the live items +
		// size are spliced per drop (SpliceLootContents). Loot ops key on the bag ENTITY handle, not this id.
		private const uint LootContainerKind = 115;

		// The loot bag's kd-tree / spatial-bounds node, decoded from the captured loot-bag tail and CONSTANT across all
		// 10 captured bags (Captures/scripts, every *-login/gameplay bag). A single leaf node:
		//   [u32 nodeTag][u32 nodeType][u64 children=0] [AABB min xyz][AABB max xyz] [float scale] [u32 0][u8 0]
		// i.e. the unit box [-0.5,0.5]^3 the client uses as the bag's pick/collision volume (raw big-endian floats, like
		// every other raw-body float). Fixed game geometry, emitted from typed fields - no embedded blob.
		private const uint NodeTag = 0x0000475C;
		private const uint NodeType = 0x000F6A19;

		public static bool IsAvailable => true; // always - the tail is constructed, never loaded

		// The 49-byte kd-tree bounds node, byte-exact vs the captured loot-bag geometry (LootBagTemplateTests).
		public static byte[] BuildBounds()
		{
			using var w = new FaolanStream();
			w.WriteUInt32(NodeTag);
			w.WriteUInt32(NodeType);
			w.WriteUInt64(0); // children / flags
			w.WriteFloat(-0.5f); // AABB min x
			w.WriteFloat(-0.5f); // AABB min y
			w.WriteFloat(-0.5f); // AABB min z
			w.WriteFloat(0.5f); // AABB max x
			w.WriteFloat(0.5f); // AABB max y
			w.WriteFloat(0.5f); // AABB max z
			w.WriteFloat(1.0f); // scale
			w.WriteUInt32(0);
			w.WriteByte(0);
			return w.ToArray();
		}

		// A fresh WorldObject carrying the generic loot bag's identity columns + the from-scratch geometry/contents tail.
		public static WorldObject Create()
		{
			return new WorldObject
			{
				Name = "Bag of loot",
				SubOpcode = SubOpcode,
				DisplayName = "<remoteformat id=\"13782\" category=\"50200\" key=\"!,1%VZc#)*ctk-zNFNxI\" knubot=\"0\"  ></remoteformat>",
				TemplateItemId = 13782,
				CarsGroup = 1,
				IsClosed = true,
				Kdtree = 0,
				KdTreeTail = [.. BuildBounds(), .. LootBagMessageCodec.BuildContents(BuildEmptyScaffold())]
			};
		}

		// A clean, empty ProtoLootBagIIR scaffold (no owner, no items) - SpliceLootContents fills in the live cash +
		// items per drop. Built via the proven typed codec, not a captured blob.
		private static ProtoLootBagIIR BuildEmptyScaffold()
		{
			return LootBagMessageCodec.BuildLootBag(0,
				new ProtoInventoryGame
				{
					InventoryId = new ProtoID32
					{
						Type = LootContainerKind,
						Instance = 0
					},
					MaxSize = DefaultMaxSize
				});
		}
	}
}
