using System;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Protobuf;
using ProtoBuf;

namespace Faolan.GameServer.Packets.Protobuf
{
	// Client->server move/drop item args. Maps 1:1 onto the client's ProtoMoveInventoryItemIIR (recovered schema,
	// docs/aoc-client-protos.proto): { source_container_id, source_item_pos, destination_container_id,
	// destination_item_pos }. The container ids use the generated ProtoID32 { type, instance } typed message.
	// The outer args stay a wire-tolerant (non-required) model rather than reusing the generated
	// ProtoMoveInventoryItemIIR, because DropItem sends only the source side and that type's IsRequired fields
	// would throw on the missing destination.
	[ProtoContract, Obsolete("Use generated")]
	public sealed class MoveItemInInventoryArgs : FaolanProtobufMessage
	{
		[ProtoMember(3)]
		public ProtoID32? DestinationContainerId { get; set; }

		[ProtoMember(1)]
		public ProtoID32? SourceContainerId { get; set; }

		[ProtoMember(4)]
		public uint DestinationItemPos { get; set; }

		[ProtoMember(2)]
		public uint SourceItemPos { get; set; }
	}

	// The TypeId of an Interact 0x09 equip move identifies the drag's window pair; Mode is the client's
	// monotonic per-session action counter, NOT a direction (armed+unarmed combat capture 2026-06-12).
	// Out-of-slot tails are [srcEquipSlot][destBagCell][0...]; into-slot tails are
	// [srcBagCell][destEquipSlot][item][item][0][flags].
	public enum MoveItemInInventoryDirection
	{
		EquippedToInventory = 42010,
		InventoryToEquipped = 42011,
		EquippedToBag = 41842, // same out-of-slot grammar via the backpack window (live client 2026-06-12)
		BagToEquipped = 41843 // same into-slot grammar via the backpack window
	}
}
