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
	[ProtoContract]
	public sealed class MoveItemInInventoryArgs : FaolanProtobufMessage
	{
		[ProtoMember(1)]
		public ProtoID32? SourceContainerId { get; set; }

		[ProtoMember(2)]
		public uint SourceItemPos { get; set; }

		[ProtoMember(3)]
		public ProtoID32? DestinationContainerId { get; set; }

		[ProtoMember(4)]
		public uint DestinationItemPos { get; set; }
	}

	public enum MoveItemInInventoryDirection
	{
		EquippedToInventory = 42010, // mode=1
		InventoryToEquipped = 42011 // mode=2
	}
}
