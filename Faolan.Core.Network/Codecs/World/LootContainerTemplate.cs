using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.World
{
	// The generic loot-container definition, built ENTIRELY FROM SCRATCH (#193): identity columns + the decoded
	// kd-tree bounds (typed, BuildBounds) + a clean ProtoLootBagIIR scaffold (LootBagContentsCodec). No embedded asset
	// and no baked capture state - the former LootBagKdTreeTail.bin carried the captured bag's stale OwnerId + a
	// captured item; the live cash + items are injected by WithLootContents into a fully-constructed bag (owner/inventory-id
	// baked); this constructs both halves so a spawned bag carries no session leftovers. WorldObjectCodec.Build
	// assembles header/transform + identity-from-columns + this tail; WithLootContents then splices the live items + cash.
	//
	// A "chest" is NOT a distinct type: every container size ships as the SAME LootBagFullUpdateIIR (typetag 0xC87A,
	// unit-box kd-tree bounds, clean scaffold) - only the RDB container TemplateItemId (the model), the overhead Name,
	// the LDB remoteformat DisplayName, and the kd-tree NodeTag differ (see the Variant table). So a boss/raid chest
	// drop is just this bag built from a bigger Variant. (agent memory loot_chest_is_lootbag_bigger_template)
	public static class LootContainerTemplate
	{
		// The container CLASS this template builds for. Not an `Opcode` declaration - WorldObjectCodec owns
		// LootBagFullUpdateIIR (one IIR, one codec); this is which class a template row is.
		public const uint ContainerClass = (uint)GameServer0X2000Opcodes.LootBagFullUpdateIIR;
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

		// The 49-byte kd-tree bounds node for a variant's NodeTag (the geometry is otherwise identical). Emitted
		// through the ONE geometry emitter (#410) so the container box and every captured object share a builder:
		// the old hand-rolled writer's `WriteUInt64(0) // children / flags` is really the record's empty name +
		// empty node list, and its "scale" is the first of the two closing words.
		public static byte[] BuildBounds(uint nodeTag)
		{
			return WorldObjectGeometryColumns.ToWire(WithBoundsColumns(new WorldObject(), nodeTag));
		}

		// The unit-box geometry columns every container shares - the pick/collision volume, [-0.5,0.5]^3.
		public static WorldObject WithBoundsColumns(WorldObject row, uint nodeTag)
		{
			row.GeometryAssetId = nodeTag;
			row.GeometryAssetType = NodeType;
			row.GeometryName = null;
			row.BoundsMinX = -0.5f;
			row.BoundsMinY = -0.5f;
			row.BoundsMinZ = -0.5f;
			row.BoundsMaxX = 0.5f;
			row.BoundsMaxY = 0.5f;
			row.BoundsMaxZ = 0.5f;
			row.GeometryWord0 = 0x3F800000; // 1.0f
			row.GeometryWord1 = 0;
			row.GeometryFlag = false;
			return row;
		}

		// A container's wire trailer: its loot contents, as the clean empty scaffold. Captured contents are session
		// state and are never persisted (#410), so every chest/bag spawns from this and WithLootContents splices the
		// live cash + items per drop.
		public static byte[] BuildEmptyContents()
		{
			return LootBagContentsCodec.Build(NewContents(0, new ProtoInventoryGame
			{
				InventoryId = new ProtoID32
				{
					Type = LootContainerKind,
					Instance = 0
				},
				MaxSize = DefaultMaxSize
			}));
		}

		// A fresh WorldObject for a specific container VARIANT (bag / Large Chest / a future raid chest) - identity
		// columns from the variant + the from-scratch bounds (its NodeTag) + a clean empty scaffold.
		public static WorldObject Create(LootContainerTemplateId typeId)
		{
			if (!Variants.TryGetValue(typeId, out var variant))
				throw new KeyNotFoundException($"LootContainerTemplateId {typeId} is not defined in the Variants table.");

			var row = new WorldObject
			{
				Name = variant.Name,
				SubOpcode = ContainerClass,
				TemplateItemId = (uint)typeId,
				CarsGroup = 1,
				IsClosed = true,
				Kdtree = 0
			};

			// The variant's display value is a captured LDB remoteformat string; lift it into the columns the same
			// way an imported object's is, so a template row builds through exactly the same path.
			if (!WorldObjectDisplay.TryApply(row, variant.DisplayName))
				throw new InvalidOperationException($"LootContainerTemplate variant {typeId} has a display value outside the inner-element grammar: {variant.DisplayName}");

			WithBoundsColumns(row, variant.NodeTag);
			return row;
		}

		// A ProtoLootBagIIR from the live drop: the cash on the corpse and its dropped items (already an assembled
		// ProtoInventoryGame, built the same way the backpack snapshot is). Looter/owner default to "no owner"
		// ({0,0}) - free-for-all loot, matching the captured bags. The empty-id messages ARE emitted (the captured
		// bags carry them), so this round-trips to the captured shape when fed the captured cash/inventory.
		public static ProtoLootBagIIR NewContents(uint cash, ProtoInventoryGame inventory)
		{
			return new ProtoLootBagIIR
			{
				Cash = cash,
				CurrentLooterId = new ProtoID32 { Type = 0, Instance = 0 },
				OwnerId = new ProtoID32 { Type = 0, Instance = 0 },
				DropperId = new ProtoID32 { Type = 0, Instance = 0 },
				AllowedLooterId = new ProtoID32 { Type = 0, Instance = 0 },
				HasMasterLooter = false,
				Inventory = inventory
			};
		}
	}
}
