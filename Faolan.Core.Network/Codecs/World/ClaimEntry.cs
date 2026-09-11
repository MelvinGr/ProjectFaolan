using System;
using System.Text;

namespace Faolan.Core.Network.Codecs.World
{
	/// <summary>
	///     One redeemable item-claim row as it appears in the CoreClaimIIR list the server sends when the client opens the
	///     Claims/Redeem window. Decoded byte-exact from the NecroL80 login_redeem-claim capture, 19 rows of 106 bytes; see
	///     docs/subsystems/item-claims.md.
	/// </summary>
	/// <remarks>
	///     The u64 after the template is the item's level rather than a stack count: redeeming a level-5 row delivered one
	///     item, its DispatchAddItem carrying no stack count. Each redeem hands over exactly one item.
	///     <para>
	///         The tail carries a charge pair, the remaining quantity and the maximum. Both 0 means infinite charges, stored
	///         null server-side and shown as an infinity in the Amount column. A multi-charge claim starts with the two
	///         equal, and the remaining word decrements on each redeem - retail re-sends the row through the redeem ack with
	///         the decremented pair, removing it at 0. In the initial list the two are equal, so a captured list still
	///         round-trips with them equal.
	///     </para>
	///     <para>
	///         DecryptionKey is the item's ProtoDecryptionKey, four big-endian u32 - the same TEA key stored as Item.Key1..4
	///         - which the client uses to decrypt the item template from its own RDB, so it must be the real key or the row
	///         renders as "No name". Key and BatchGuid, which is constant per response, are each emitted twice on the wire
	///         as mirror pairs. Type is 6 for an account-bound claim and 4 for a character-bound one, the Bound To column.
	///     </para>
	/// </remarks>
	public sealed record ClaimEntry(uint Index, uint ItemTemplateId, ulong Level, string Code, byte[] DecryptionKey, byte[] BatchGuid, uint Type = 6, uint Quantity = 0, uint MaxQuantity = 0)
	{
		public const int BatchGuidLength = 8;
		public const int CodeLength = 4;
		public const int KeyLength = 16;

		/// <summary>The 4-character redemption code as raw ASCII bytes, padded or truncated to <see cref="CodeLength" />.</summary>
		public byte[] CodeBytes()
		{
			var bytes = new byte[CodeLength];
			var src = Encoding.Latin1.GetBytes(Code ?? "");
			Array.Copy(src, bytes, Math.Min(src.Length, CodeLength));
			return bytes;
		}
	}
}
