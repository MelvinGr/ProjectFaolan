using System.Collections.Generic;
using System.Text;
using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// The claim/redeem list the server sends when the client opens the Claims window (it first sends a CoreClaimIIR
	// request, sub 0x0007; the server answers with this, sub 0x0001). Decoded byte-exact from the NecroL80
	// login_redeem-claim capture (docs/subsystems/item-claims.md), frame 399: 19 rows of 106 bytes.
	//
	// Body = [CoreClaimIIR opcode][0xC350 Character][handle][u16 sub=0x0001][u32 count][ count * 106-byte entry ].
	// Entry = [u32 index][u32 tpl][u32 tpl(dup)][u64 level][4B code][16B key][16B key(dup)][8B guid][8B guid(dup)]
	//         [u32 type][u32 qty][u32 qty(dup)][u32 0][u32 0][u32 0][u32 1][u32 0][u16 0].
	// The u64 is the item LEVEL (not the stack count - see ClaimEntry); the mirrored qty pair is the amount. The
	// 16-byte key is the item's ProtoDecryptionKey (= Item.Key1..4); the trailing tail (…,1,0,0) is a fixed
	// availability/flags block. All preserved verbatim (byte-exact test).
	public static class CoreClaimListCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.CoreClaimIIR;

		// Sub-opcodes carried in the u16 after the endpoint (method indices within the InventoryClaims IIR).
		public const ushort SubList = 0x0001; // s2c: the claim list (this codec)
		public const ushort SubNewClaims = 0x0009; // s2c: "you have new claims" glow (CoreClaimCodec.Build)
		public const ushort SubRedeem = 0x0003; // c2s: redeem the 1-based claim index
		public const ushort SubRedeemAck = 0x0005; // s2c: multi-charge redeem ack (one entry, decremented amount)
		public const ushort SubRequest = 0x0007; // c2s: "send me my claims"

		private const int EntrySize = 106;

		public static byte[] Build(uint handle, IReadOnlyList<ClaimEntry> claims)
		{
			var stream = new FaolanStream().WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(handle).WriteUInt16(SubList).WriteUInt32(claims.Count);

			foreach (var claim in claims)
				WriteEntry(stream, claim);

			return stream.ToArray();
		}

		// CoreClaimIIR sub 0x0009 - the "new claims available" GLOW push that lights the client's Claims/Redeem
		// button. Body = [opcode][Character][handle][u16 sub], no payload. Retail sends it at world entry ONLY when
		// the account has unseen claims: present in the Faolanx login capture (new claims), absent in the NecroL80
		// login_redeem-claim capture (already seen), so it is gated on an IsNew flag rather than pushed
		// unconditionally. (Was a separate one-method CoreClaimCodec that already borrowed SubNewClaims from here -
		// one opcode, one codec.) See docs/subsystems/item-claims.md.
		public static byte[] BuildNewClaimsGlow(uint handle)
		{
			return new FaolanStream().WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(handle).WriteUInt16(SubNewClaims).ToArray();
		}

		// The sub-0x05 redeem ack for a multi-charge claim: a single entry echoing the row with its decremented
		// remaining amount (the client refreshes that row in place). Byte-exact against frames 396/540.
		public static byte[] BuildRedeemAck(uint handle, ClaimEntry claim)
		{
			var stream = new FaolanStream().WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(handle).WriteUInt16(SubRedeemAck);

			WriteEntry(stream, claim);
			return stream.ToArray();
		}

		// Decodes a CoreClaimIIR list payload (the bytes after the 0x2000 sub-header's u32 length prefix, i.e. starting
		// at the opcode). Returns false if it isn't a sub-0x0001 list. Used by the importer to harvest each item's key
		// and to seed AccountClaim rows.
		public static bool TryParse(byte[] payload, out uint handle, out IReadOnlyList<ClaimEntry> claims)
		{
			handle = 0;
			claims = [];
			if (payload == null || payload.Length < 18)
				return false;

			var stream = new FaolanStream(payload);
			if (stream.ReadUInt32() != Opcode)
				return false;

			stream.ReadUInt32(); // 0xC350 Character
			handle = stream.ReadUInt32();
			if (stream.ReadUInt16() != SubList)
				return false;

			var count = stream.ReadUInt32();
			var list = new List<ClaimEntry>((int)count);
			for (var i = 0u; i < count; i++)
			{
				if (stream.Length - stream.Position < EntrySize)
					return false;

				var index = stream.ReadUInt32();
				var template = stream.ReadUInt32();
				stream.ReadUInt32(); // template (dup)
				var level = stream.ReadUInt64();
				var code = Encoding.Latin1.GetString(stream.ReadArray(ClaimEntry.CodeLength)).TrimEnd('\0');
				var key = stream.ReadArray(ClaimEntry.KeyLength);
				stream.ReadArray(ClaimEntry.KeyLength); // key (dup)
				var guid = stream.ReadArray(ClaimEntry.BatchGuidLength);
				stream.ReadArray(ClaimEntry.BatchGuidLength); // guid (dup)
				var type = stream.ReadUInt32();
				var quantity = stream.ReadUInt32();
				var maxQuantity = stream.ReadUInt32();
				stream.ReadUInt32(); // 0
				stream.ReadUInt32(); // 0
				stream.ReadUInt32(); // 0
				stream.ReadUInt32(); // 1 (fixed flag)
				stream.ReadUInt32(); // 0
				stream.ReadUInt16(); // 0

				list.Add(new ClaimEntry(index, template, level, code, key, guid, type, quantity, maxQuantity));
			}

			claims = list;
			return true;
		}

		private static void WriteEntry(FaolanStream stream, ClaimEntry claim)
		{
			var key = claim.DecryptionKey;
			var guid = claim.BatchGuid;

			stream
				.WriteUInt32(claim.Index)
				.WriteUInt32(claim.ItemTemplateId)
				.WriteUInt32(claim.ItemTemplateId)
				.WriteUInt64(claim.Level)
				.WriteArray(claim.CodeBytes())
				.WriteArray(key)
				.WriteArray(key)
				.WriteArray(guid)
				.WriteArray(guid)
				.WriteUInt32(claim.Type)
				.WriteUInt32(claim.Quantity)
				.WriteUInt32(claim.MaxQuantity)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(1)
				.WriteUInt32(0)
				.WriteUInt16(0);
		}
	}
}
