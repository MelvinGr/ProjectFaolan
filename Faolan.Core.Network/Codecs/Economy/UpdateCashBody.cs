namespace Faolan.Core.Network.Codecs.Economy
{
	public static class UpdateCashBody
	{
		public static void WriteUpdateCashBody(FaolanStream s, ulong balanceTin, UpdateCashReason reason)
		{
			s.WriteUInt64(balanceTin).WriteUInt32(reason);
		}
	}

	// The unit is TIN, the game's smallest coin (100 tin = 1 copper, ...). The 12-byte 0x2005 DispatchUpdateCash
	// body [u64 balanceTin][u32 reason] - the absolute wallet balance, not a delta. Extracted from the
	// GameServerListener.UpdateCash partial; the listener keeps the framing + send. Golden: UpdateCashTests.
	public enum UpdateCashReason : uint
	{
		Spend = 0, // also used for syncs that don't celebrate a gain
		Unknown2 = 2, // seen once in the captures; semantics unknown
		Gain = 3
	}
}
