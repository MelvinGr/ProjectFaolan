namespace Faolan.Core.Network.Codecs
{
	// The DispatchRemoveItem (s2c 0x201F) body: [inventory ID32: containerType, charId][u32 itemPos][u32 amount].
	// Extracted from the GameServerListener.RemoveItem partial; the listener keeps the framing + send. Golden:
	// DispatchRemoveItemTests.
	public static class RemoveItemBody
	{
		public static void WriteRemoveItemBody(FaolanStream s, uint containerType, uint charId, uint itemPos, uint amountToRemove)
		{
			s.WriteUInt32(containerType).WriteUInt32(charId).WriteUInt32(itemPos).WriteUInt32(amountToRemove);
		}
	}
}
