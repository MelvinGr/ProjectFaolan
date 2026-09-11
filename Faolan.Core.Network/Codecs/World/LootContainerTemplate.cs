using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.World
{
	/// <summary>
	///     The generic loot-container definition, built entirely from scratch: identity columns, the typed kd-tree bounds
	///     (<see cref="BuildBounds()" />) and a clean ProtoLootBagIIR scaffold (<see cref="LootBagContentsCodec" />). No
	///     embedded asset and no baked capture state, so a spawned bag carries no session leftovers.
	/// </summary>
	/// <remarks>
	///     <see cref="WorldObjectCodec" />.Build assembles the header, transform and identity from columns plus this tail;
	///     the live cash and items are spliced in per drop.
	///     <para>
	///         A chest is not a distinct type: every container size ships as the same LootBagFullUpdateIIR, with the same
	///         typetag, unit-box kd-tree bounds and clean scaffold. Only the RDB container TemplateItemId (the model), the
	///         overhead name, the LDB remoteformat display name and the kd-tree node tag differ, so a boss or raid chest drop
	///         is this bag built from a bigger variant.
	///     </para>
	/// </remarks>
	public static class LootContainerTemplate
	{
		/// <summary>
		///     The container class this template builds for. Not an opcode declaration: <see cref="WorldObjectCodec" /> owns
		///     LootBagFullUpdateIIR, one IIR to one codec, and this is which class a template row is.
		/// </summary>
		public const uint ContainerClass = (uint)IirTypes.LootBagFullUpdateIIR;
		private const uint DefaultMaxSize = 10;

		/// <summary>
		///     The loot inventory's container kind, the ID32 type, on the wire. The instance is left 0 here and the live
		///     items and size are spliced per drop. Loot operations key on the bag entity handle rather than this id.
		/// </summary>
		private const uint LootContainerKind = (uint)ContainerKind.Loot;

		/// <summary>
		///     The kd-tree node type, constant across every captured container. The 49-byte bounds node is a single leaf -
		///     <c>[u32 nodeTag][u32 nodeType][u64 children=0][AABB min xyz][AABB max xyz][float scale][u32 0][u8 0]</c> -
		///     the unit box the client uses as the pick and collision volume, in raw big-endian floats. It is fixed geometry
		///     emitted from typed fields; a chest's bigger look comes from its template model rather than the box.
		/// </summary>
		private const uint NodeType = 0x000F6A19;

		/// <summary>
		///     Container variants keyed by template id: the per-variant fields that differ, everything else - wire type,
		///     typetag, bounds geometry, scaffold, closed state and CarsGroup - being shared. DisplayName is the client's LDB
		///     remoteformat, its id the TemplateItemId and its category 50200, and its key the LDB lookup key lifted from a
		///     capture.
		/// </summary>
		public static readonly ImmutableDictionary<LootContainerTemplateId, (string Name, string DisplayName, uint NodeTag)> Variants =
			new Dictionary<LootContainerTemplateId, (string Name, string DisplayName, uint NodeTag)>
			{
				[LootContainerTemplateId.Bag] = ("Bag of loot", "<remoteformat id=\"13782\" category=\"50200\" key=\"!,1%VZc#)*ctk-zNFNxI\" knubot=\"0\"  ></remoteformat>", 0x0000475C),
				[LootContainerTemplateId.LargeChest] = ("Large Chest", "<remoteformat id=\"13781\" category=\"50200\" key=\"]uYN6#x&#39;&quot;XEXwPlC#M,o\" knubot=\"0\"  ></remoteformat>", 0x0000475D)
			}.ToImmutableDictionary();

		/// <summary>
		///     The 49-byte kd-tree bounds node for the default bag variant, byte-exact against the captured loot-bag geometry
		///     (LootBagTemplateTests). See the node-tag overload for other variants.
		/// </summary>
		public static byte[] BuildBounds()
		{
			return BuildBounds(Variants[LootContainerTemplateId.Bag].NodeTag);
		}

		/// <summary>
		///     The 49-byte kd-tree bounds node for a variant's node tag; the geometry is otherwise identical. It is emitted
		///     through the one geometry emitter so the container box and every captured object share a builder - what a
		///     hand-rolled writer reads as a children-and-flags word is really the record's empty name and empty node list,
		///     and its scale is the first of the two closing words.
		/// </summary>
		public static byte[] BuildBounds(uint nodeTag)
		{
			return WorldObjectGeometryColumns.ToWire(WithBoundsColumns(new WorldObject(), nodeTag));
		}

		/// <summary>The unit-box geometry columns every container shares - the pick and collision volume.</summary>
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

		/// <summary>
		///     A container's wire trailer: its loot contents, as the clean empty scaffold. Captured contents are session
		///     state and are never persisted, so every chest and bag spawns from this and the live cash and items are spliced
		///     in per drop.
		/// </summary>
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

		/// <summary>
		///     A fresh WorldObject for a specific container variant: identity columns from the variant, the from-scratch
		///     bounds for its node tag, and a clean empty scaffold.
		/// </summary>
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
				// A bag is closed, which is the build default rather than a column; see WorldObjectColumns.ToMessage.
				Kdtree = 0
			};

			// The variant's display value is a captured LDB remoteformat string; lift it into the columns the same way
			// an imported object's is, so a template row builds through exactly the same path.
			if (!WorldObjectDisplay.TryApply(row, variant.DisplayName))
				throw new InvalidOperationException($"LootContainerTemplate variant {typeId} has a display value outside the inner-element grammar: {variant.DisplayName}");

			WithBoundsColumns(row, variant.NodeTag);
			return row;
		}

		/// <summary>
		///     A ProtoLootBagIIR from the live drop: the cash on the corpse and its dropped items, already an assembled
		///     ProtoInventoryGame built the way the backpack snapshot is. Looter and owner default to no owner, which is
		///     free-for-all loot and matches the captured bags. The empty-id messages are emitted, since the captured bags
		///     carry them, so this round-trips to the captured shape when fed the captured cash and inventory.
		/// </summary>
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
