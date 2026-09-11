namespace Faolan.Core.Enums
{
	// The u16 method index a CoreClaimIIR carries after the endpoint - the Claims/Redeem window's little
	// request/response protocol, decoded from the NecroL80 login_redeem-claim capture
	// (docs/subsystems/item-claims.md). These are method indices within the InventoryClaims IIR, not entries in
	// IirTypes, which is why they are their own enum rather than members of an opcode catalog.
	//
	// Both directions are here deliberately: the pair is what makes the exchange readable (Request -> List,
	// Redeem -> RedeemAck), and splitting c2s from s2c would hide that.
	public enum CoreClaimSubOpcode : ushort
	{
		List = 0x0001, // s2c: the claim list

		// s2c, sent once per login. The client reads it into the same slot the Redeem index goes to (`ReadSubClass`
		// FUN_007AEC50 shares one `case 2: case 3:` arm reading a single u32), so the two really are one body. What
		// the number is remains undecoded [V0]: NecroL80's four logins carry 112, 114, 116 and 118 - exactly +2 per
		// session - and HeraldL carries 90, i.e. a per-account monotonic counter rather than anything about the claim
		// list beside it, which is the same 19 entries every time.
		LoginCounter = 0x0002,
		Redeem = 0x0003, // c2s: redeem the 1-based claim index
		RedeemAck = 0x0005, // s2c: multi-charge redeem ack (one entry, decremented amount)
		Request = 0x0007, // c2s: "send me my claims"
		NewClaims = 0x0009, // s2c: the "you have new claims" glow on the Claims button

		// c2s, payload-less. The name records the position it was observed in, not a decoded meaning [V0]: the client
		// sends it 0.0s after the server's List lands and ~20s before any Redeem, so it is not the redeem itself. n=1
		// in the whole corpus (xxx_mage-necromancer_login_play4 frame 11237, the only capture of a claims window being
		// opened rather than replayed), so treat the reading as unconfirmed - what is certain is the shape, because
		// the length word states it. Nothing follows it on the wire and the client goes on to redeem claims normally,
		// so ignoring it inbound is what retail's own client tolerates.
		ListAck = 0x000A
	}
}
