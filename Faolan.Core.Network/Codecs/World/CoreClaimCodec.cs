using System;
using System.Collections.Generic;
using System.Text;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// CoreClaimIIR (0x06EC1255) - the Claims/Redeem window's little request/response protocol, decoded byte-exact from
	// the NecroL80 login_redeem-claim capture (docs/subsystems/item-claims.md), frame 399: 19 rows of 106 bytes.
	//
	// A FAMILY (#530 rule 5a): every message is [opcode][0xC350 Character][handle][u16 sub][payload], discriminated by
	// the sub word, so one codec parses them all and returns the base CoreClaimIIR with a record per sub. The base is
	// not fiction - the carrier and the handle really are shared.
	//
	// List entry = [u32 index][u32 tpl][u32 tpl(dup)][u64 level][4B code][16B key][16B key(dup)][8B guid][8B guid(dup)]
	//              [u32 type][u32 qty][u32 qty(dup)][u32 0][u32 0][u32 0][u32 1][u32 0][u16 0].
	// The u64 is the item LEVEL (not the stack count - see ClaimEntry); the mirrored qty pair is the amount. The
	// 16-byte key is the item's ProtoDecryptionKey (= Item.Key1..4); the trailing tail (…,1,0,0) is a fixed
	// availability/flags block. All preserved verbatim (byte-exact test).
	public static class CoreClaimCodec
	{
		public const uint Opcode = (uint)IirTypes.CoreClaimIIR;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(CoreClaimIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint handle, IReadOnlyList<ClaimEntry> claims)
		{
			return Build(new CoreClaimListIIR(handle, claims));
		}

		public static CoreClaimIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(CoreClaimCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static CoreClaimIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "CoreClaimIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			var subOffset = r.Position;
			var sub = (CoreClaimSubOpcode)r.ReadU16Be();
			switch (sub)
			{
				case CoreClaimSubOpcode.List:
				{
					var count = r.ReadU32Be();
					var claims = new List<ClaimEntry>((int)count);
					for (var i = 0u; i < count; i++)
						claims.Add(ReadEntry(ref r));
					return new CoreClaimListIIR(handle, claims);
				}

				case CoreClaimSubOpcode.RedeemAck:
				{
					return new CoreClaimRedeemAckIIR(handle, ReadEntry(ref r));
				}

				case CoreClaimSubOpcode.NewClaims:
				{
					return new CoreClaimNewClaimsIIR(handle);
				}

				case CoreClaimSubOpcode.Request:
				{
					return new CoreClaimRequestIIR(handle);
				}

				case CoreClaimSubOpcode.Redeem:
				{
					return new CoreClaimRedeemIIR(handle, r.ReadU32Be());
				}

				case CoreClaimSubOpcode.LoginCounter:
				{
					return new CoreClaimLoginCounterIIR(handle, r.ReadU32Be());
				}

				default:
				{
					throw CodecParseException.Unexpected(nameof(CoreClaimCodec), subOffset, "a modelled CoreClaim sub-opcode", $"0x{(ushort)sub:X4}");
				}
			}
		}

		internal static void Write(FaolanStream s, CoreClaimIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.Handle).WriteUInt16((ushort)v.Sub);
			switch (v)
			{
				case CoreClaimListIIR list:
				{
					s.WriteUInt32(list.Claims.Count);
					foreach (var claim in list.Claims)
						WriteEntry(s, claim);
					break;
				}

				case CoreClaimRedeemAckIIR ack:
				{
					WriteEntry(s, ack.Claim);
					break;
				}

				case CoreClaimRedeemIIR redeem:
				{
					s.WriteUInt32(redeem.Index);
					break;
				}

				case CoreClaimLoginCounterIIR counter:
				{
					s.WriteUInt32(counter.Value);
					break;
				}
			}
		}

		private static ClaimEntry ReadEntry(ref SpanReader r)
		{
			var index = r.ReadU32Be();
			var template = r.ReadU32Be();
			r.ExpectU32Be(template, "the mirrored item-template id");
			var level = r.ReadU64Be();
			var code = Encoding.Latin1.GetString(r.ReadBytes(ClaimEntry.CodeLength)).TrimEnd('\0');
			var key = r.ReadBytes(ClaimEntry.KeyLength).ToArray();
			r.ReadBytes(ClaimEntry.KeyLength); // key (dup)
			var guid = r.ReadBytes(ClaimEntry.BatchGuidLength).ToArray();
			r.ReadBytes(ClaimEntry.BatchGuidLength); // guid (dup)
			var type = r.ReadU32Be();
			var quantity = r.ReadU32Be();
			var maxQuantity = r.ReadU32Be();
			r.ExpectU32Be(0, "claim tail word 0");
			r.ExpectU32Be(0, "claim tail word 1");
			r.ExpectU32Be(0, "claim tail word 2");
			r.ExpectU32Be(1, "claim tail flag");
			r.ExpectU32Be(0, "claim tail word 3");
			r.ExpectU16Be(0, "claim tail half-word");
			return new ClaimEntry(index, template, level, code, key, guid, type, quantity, maxQuantity);
		}

		private static void WriteEntry(FaolanStream s, ClaimEntry claim)
		{
			var key = claim.DecryptionKey;
			var guid = claim.BatchGuid;

			s.WriteUInt32(claim.Index)
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

	// The CoreClaimIIR family base: the carrier and the addressed handle every sub shares.
	public abstract record CoreClaimIIR(uint Handle)
	{
		public abstract CoreClaimSubOpcode Sub { get; }
	}

	// sub 0x0001 s2c - the claim list the server sends when the client opens the Claims window.
	public sealed record CoreClaimListIIR(uint Handle, IReadOnlyList<ClaimEntry> Claims) : CoreClaimIIR(Handle)
	{
		public override CoreClaimSubOpcode Sub => CoreClaimSubOpcode.List;
	}

	// sub 0x0005 s2c - the redeem ack for a multi-charge claim: a single entry echoing the row with its decremented
	// remaining amount (the client refreshes that row in place). Byte-exact against frames 396/540.
	public sealed record CoreClaimRedeemAckIIR(uint Handle, ClaimEntry Claim) : CoreClaimIIR(Handle)
	{
		public override CoreClaimSubOpcode Sub => CoreClaimSubOpcode.RedeemAck;
	}

	// sub 0x0009 s2c - the "new claims available" GLOW push that lights the client's Claims/Redeem button. No payload.
	// Retail sends it at world entry ONLY when the account has unseen claims: present in the Faolanx login capture
	// (new claims), absent in the NecroL80 login_redeem-claim capture (already seen), so it is gated on an IsNew flag
	// rather than pushed unconditionally. See docs/subsystems/item-claims.md.
	public sealed record CoreClaimNewClaimsIIR(uint Handle) : CoreClaimIIR(Handle)
	{
		public override CoreClaimSubOpcode Sub => CoreClaimSubOpcode.NewClaims;
	}

	// sub 0x0007 c2s - "send me my claims". No payload.
	public sealed record CoreClaimRequestIIR(uint Handle) : CoreClaimIIR(Handle)
	{
		public override CoreClaimSubOpcode Sub => CoreClaimSubOpcode.Request;
	}

	// sub 0x0003 c2s - redeem the 1-based claim index into the last-sent list.
	public sealed record CoreClaimRedeemIIR(uint Handle, uint Index) : CoreClaimIIR(Handle)
	{
		public override CoreClaimSubOpcode Sub => CoreClaimSubOpcode.Redeem;
	}

	// sub 0x0002 s2c - the once-per-login counter that shares the Redeem body. See CoreClaimSubOpcode.LoginCounter
	// for what is and is not known about the number.
	public sealed record CoreClaimLoginCounterIIR(uint Handle, uint Value) : CoreClaimIIR(Handle)
	{
		public override CoreClaimSubOpcode Sub => CoreClaimSubOpcode.LoginCounter;
	}
}
