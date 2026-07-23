using System.Collections.Generic;
using System.Collections.Immutable;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.World
{
	// The generic loot-container definition, built ENTIRELY FROM SCRATCH (#193): identity columns + the decoded
	// kd-tree bounds (typed, BuildBounds) + a clean ProtoLootBagIIR scaffold (LootBagMessageCodec). No embedded asset
	// and no baked capture state - the former LootBagKdTreeTail.bin carried the captured bag's stale OwnerId + a
	// captured item; the live cash + items are injected by WithLootContents into a fully-constructed bag (owner/inventory-id
	// baked); this constructs both halves so a spawned bag carries no session leftovers. WorldObjectBuilder.BuildBody
	// assembles header/transform + identity-from-columns + this tail; WithLootContents then splices the live items + cash.
	//
	// A "chest" is NOT a distinct type: every container size ships as the SAME LootBagFullUpdateIIR (typetag 0xC87A,
	// unit-box kd-tree bounds, clean scaffold) - only the RDB container TemplateItemId (the model), the overhead Name,
	// the LDB remoteformat DisplayName, and the kd-tree NodeTag differ (see the Variant table). So a boss/raid chest
	// drop is just this bag built from a bigger Variant. (agent memory loot_chest_is_lootbag_bigger_template)
	public static class LootContainerTemplate
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.LootBagFullUpdateIIR; // LootBagFullUpdateIIR
		public const uint TypeTag = 0x0000C87A; // the loot typetag (WorldLoot.LootTypeTag)
		private const uint DefaultMaxSize = 10;

		// The loot inventory's container KIND (ID32 type) on the wire; the instance is left 0 here and the live items +
		// size are spliced per drop (WithLootContents). Loot ops key on the bag ENTITY handle, not this id.
		private const uint LootContainerKind = (uint)ContainerKind.Loot;

		// The kd-tree node TYPE, constant across every captured container. The 49-byte bounds node is a single leaf:
		//   [u32 nodeTag][u32 nodeType][u64 children=0] [AABB min xyz][AABB max xyz] [float scale] [u32 0][u8 0]
		// i.e. the unit box [-0.5,0.5]^3 the client uses as the pick/collision volume (raw big-endian floats). Fixed
		// geometry, emitted from typed fields - the bigger LOOK of a chest comes from its template MODEL, not the box.
		private const uint NodeType = 0x000F6A19;

		// Container variants keyed by template id: the per-variant fields that differ (everything else - wire
		// type/typetag/bounds geometry/scaffold/IsClosed/CarsGroup - is shared). DisplayName is the client's LDB
		// remoteformat (id = TemplateItemId, category 50200); the key is the LDB lookup key, lifted from a capture.
		public static readonly ImmutableDictionary<LootContainerTemplateId, (string Name, string DisplayName, uint NodeTag)> Variants =
			new Dictionary<LootContainerTemplateId, (string Name, string DisplayName, uint NodeTag)>
			{
				[LootContainerTemplateId.Bag] = ("Bag of loot", "<remoteformat id=\"13782\" category=\"50200\" key=\"!,1%VZc#)*ctk-zNFNxI\" knubot=\"0\"  ></remoteformat>", 0x0000475C),
				[LootContainerTemplateId.LargeChest] = ("Large Chest", "<remoteformat id=\"13781\" category=\"50200\" key=\"]uYN6#x&#39;&quot;XEXwPlC#M,o\" knubot=\"0\"  ></remoteformat>", 0x0000475D)
			}.ToImmutableDictionary();

		// The 49-byte kd-tree bounds node for the default (Bag) variant, byte-exact vs the captured loot-bag geometry
		// (LootBagTemplateTests). See the NodeTag overload for other variants.
		public static byte[] BuildBounds()
		{
			return BuildBounds(Variants[LootContainerTemplateId.Bag].NodeTag);
		}

		// The 49-byte kd-tree bounds node for a variant's NodeTag (the geometry is otherwise identical).
		public static byte[] BuildBounds(uint nodeTag)
		{
			using var w = new FaolanStream();
			w.WriteUInt32(nodeTag);
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

		// A fresh WorldObject for a specific container VARIANT (bag / Large Chest / a future raid chest) - identity
		// columns from the variant + the from-scratch bounds (its NodeTag) + a clean empty scaffold.
		public static WorldObject Create(LootContainerTemplateId typeId)
		{
			if (!Variants.TryGetValue(typeId, out var variant))
				throw new KeyNotFoundException($"LootContainerTemplateId {typeId} is not defined in the Variants table.");

			return new WorldObject
			{
				Name = variant.Name,
				SubOpcode = Opcode,
				DisplayName = variant.DisplayName,
				TemplateItemId = (uint)typeId,
				CarsGroup = 1,
				IsClosed = true,
				Kdtree = 0,
				KdTreeTail = [.. BuildBounds(variant.NodeTag), .. LootBagMessageCodec.BuildContents(BuildEmptyScaffold())]
			};
		}

		// A clean, empty ProtoLootBagIIR scaffold (no owner, no items) - WithLootContents fills in the live cash +
		// items per drop. Built via the proven typed codec, not a captured blob.
		private static ProtoLootBagIIR BuildEmptyScaffold()
		{
			return LootBagMessageCodec.BuildLootBag(0, new ProtoInventoryGame
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
