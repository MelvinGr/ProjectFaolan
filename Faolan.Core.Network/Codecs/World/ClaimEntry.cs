using System;

namespace Faolan.Core.Network.Codecs.World
{
	// One redeemable item-claim row as it appears in the CoreClaimIIR list (sub 0x0001) the server sends when the
	// client opens the Claims/Redeem window. Decoded byte-exact from the NecroL80 login_redeem-claim capture
	// (docs/subsystems/item-claims.md): 19 rows of 106 bytes each.
	//
	// Field semantics (confirmed against the capture's redeem, frame 462): the u64 after the template is the item's
	// LEVEL, not the stack count - redeeming idx 6 (Level 5) delivered ONE Slavemaster's Staff (the DispatchAddItem
	// carried no stat-26 stack count). Each redeem hands over exactly ONE item.
	//
	// The tail carries a CHARGE pair [Quantity remaining][MaxQuantity]. Both 0 = INFINITE charges (stored null server-side; the
	// "Amount" column shows ∞); for a multi-charge claim ("Double XP", 20 potions) they start equal (20/20) and the
	// remaining word decrements on each redeem - retail re-sends the row via the sub-0x05 ack with [remaining-1][max]
	// (frames 396/540 show 19/20 then 18/20), removing it at 0. In the INITIAL list remaining == max, so a captured
	// list still round-trips with Quantity == MaxQuantity.
	//
	// DecryptionKey (16 bytes = the item's ProtoDecryptionKey, 4×u32 big-endian - the SAME TEA key stored as
	// Item.Key1..4) is what the client uses to decrypt the item template from its own RDB, so it MUST be the real key
	// or the client renders the row as "No name". Confirmed against Item 4208485 (Slavemaster's Staff). Key and
	// BatchGuid (8B, constant per response) are each emitted twice on the wire (mirror pairs). Type is 6 for an
	// account-bound claim, 4 for a character-bound one (the "Bound To" column).
	public sealed record ClaimEntry(
		uint Index,
		uint ItemTemplateId,
		ulong Level,
		string Code,
		byte[] DecryptionKey,
		byte[] BatchGuid,
		uint Type = 6,
		uint Quantity = 0,
		uint MaxQuantity = 0)
	{
		public const int CodeLength = 4;
		public const int KeyLength = 16;
		public const int BatchGuidLength = 8;

		// The 4-char redemption code as its raw ASCII bytes (padded/truncated to CodeLength).
		public byte[] CodeBytes()
		{
			var bytes = new byte[CodeLength];
			var src = System.Text.Encoding.ASCII.GetBytes(Code ?? "");
			Array.Copy(src, bytes, Math.Min(src.Length, CodeLength));
			return bytes;
		}
	}
}
