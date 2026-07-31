namespace Faolan.Core.Enums
{
	// The u16 method index a CoreClaimIIR carries after the endpoint - the Claims/Redeem window's little
	// request/response protocol, decoded from the NecroL80 login_redeem-claim capture
	// (docs/subsystems/item-claims.md). These are method indices WITHIN the InventoryClaims IIR, not entries in
	// IirTypes, which is why they are their own enum rather than members of an opcode catalog.
	//
	// Both directions are here deliberately: the pair is what makes the exchange readable (Request -> List,
	// Redeem -> RedeemAck), and splitting c2s from s2c would hide that.
	public enum CoreClaimSubOpcode : ushort
	{
		List = 0x0001, // s2c: the claim list
		Redeem = 0x0003, // c2s: redeem the 1-based claim index
		RedeemAck = 0x0005, // s2c: multi-charge redeem ack (one entry, decremented amount)
		Request = 0x0007, // c2s: "send me my claims"
		NewClaims = 0x0009 // s2c: the "you have new claims" glow on the Claims button
	}
}
