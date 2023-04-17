using Faolan.Core.Network.Packets;
using ProtoBuf;

namespace Faolan.GameServer.Packets.Protobuf
{
	[ProtoContract]
	public sealed class MoveItemInInventoryArgs : FaolanProtobufMessage
	{
		[ProtoMember(1)]
		public ItemReference? Source { get; set; }

		[ProtoMember(3)]
		public ItemReference? Target { get; set; }

		[ProtoMember(2)]
		public uint FromSlot { get; set; }

		[ProtoMember(4)]
		public uint ToSlot { get; set; }
	}

	[ProtoContract]
	public sealed class ItemReference : FaolanProtobufMessage
	{
		// Current name is provisional:
		// random-actions + 0x737A6DF9 analysis show the same value reused across sibling
		// container blocks (101/104/112/113), so this may be a shared container-owner/root
		// handle rather than a per-item instance id.
		[ProtoMember(2)]
		public uint ItemHandle { get; set; }

		[ProtoMember(1)]
		public uint SlotOrContainer { get; set; }
	}

	public enum MoveItemInInventoryDirection
	{
		EquippedToInventory = 42010, // mode=1
		InventoryToEquipped = 42011 // mode=2
	}
}
