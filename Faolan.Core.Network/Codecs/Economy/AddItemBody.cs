using System.IO;
using Faolan.Core.Enums;
using Faolan.Core.Protobuf;
using ProtoBuf;

namespace Faolan.Core.Network.Codecs.Economy
{
	// The DispatchAddItem (s2c 0x2020) body: an inventory ID32 + slot + the length-prefixed ProtoInventoryGameEntry
	// (the same entry the FullChar snapshot carries) + a feedback flag. Extracted from the GameServerListener.AddItem
	// partial so the body builds without a live client (the listener keeps the framing + send). Byte-exact in
	// DispatchAddItemTests.
	public static class AddItemBody
	{
		public static byte[] SerializeInventoryEntry(ProtoInventoryGameEntry entry)
		{
			using var ms = new MemoryStream();
			Serializer.Serialize(ms, entry);
			return ms.ToArray();
		}

		public static void WriteAddItemBody(FaolanStream s, ContainerKind containerType, uint charId, uint itemPos, byte[] entry, uint feedback = 0)
		{
			s.WriteUInt32(containerType).WriteUInt32(charId).WriteUInt32(itemPos).WriteArrayUInt32Length(entry).WriteUInt32(feedback);
		}
	}
}
