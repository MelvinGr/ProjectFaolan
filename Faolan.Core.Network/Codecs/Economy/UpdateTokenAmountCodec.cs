using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Economy
{
	// DispatchUpdateTokenAmount (s2c 0x204D) - one token pool's RUNNING TOTALS, not a delta:
	//   [u8 tokenId][u64 gained][u64 spent][u8 includeBonus]
	// The client names the four fields itself (`DispatchUpdateTokenAmount(tokenID, gained, spent, includeBonus)`,
	// docs/re/aoc-client-rpc-methods.json), and the widths are forced by the corpus: across the 10 captured frames
	// the ONLY bytes that vary are offset 0 and offset 8. A big-endian field's varying byte is its LAST, so field 1
	// ends at 0 (one byte wide) and field 2 ends at 8 (offsets 1..8, a u64 - the same width `DispatchUpdateCash`
	// gives its balance). That leaves 9..16 for `spent` and 17 for the flag, and 1+8+8+1 is the only split of 18
	// that fits. `spent` and `includeBonus` are 0 in every captured frame, so their widths are inferred, not
	// measured. [V3: UpdateTokenAmountTests.UpdateTokenAmount_RoundTripsEveryCapturedBody]
	//
	// The pool an id names comes from the client's own table, RDB 1000520 - see `TokenPool`. The send is
	// event-driven, one frame per event: `VeteranPoints` 0/0 at a new character's first world entry,
	// `TeleportCoins` / `LotteryToken` on a login-reward claim, and `AltAdvancementGeneralToken` running
	// 1 -> 2 for one character. So `gained` is a lifetime TOTAL, not a delta.
	//
	// ⛔ The AA pair was recorded here as "over two Destiny quest completions", which read as a RULE and is
	// refuted by its own control: 32 quest completions in the corpus, 26 of them Destiny steps, 2 paid. Nothing
	// we hold separates the two - docs/re/alternate-advancement-perks.md, Gitea #150.
	// The server never sends this today - we model no token pool - so the codec exists to write the decode down
	// where the next caller looks.
	public static class UpdateTokenAmountCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchUpdateTokenAmount;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(UpdateTokenAmount value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static UpdateTokenAmount Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(UpdateTokenAmountCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static UpdateTokenAmount Read(ref SpanReader r)
		{
			return new UpdateTokenAmount((TokenPool)r.ReadU8(), r.ReadU64Be(), r.ReadU64Be(), r.ReadU8() != 0);
		}

		internal static void Write(FaolanStream s, UpdateTokenAmount v)
		{
			s.WriteByte((byte)v.TokenId).WriteUInt64(v.Gained).WriteUInt64(v.Spent).WriteByte((byte)(v.IncludeBonus ? 1 : 0));
		}
	}

	// DispatchUpdateTokenAmount (0x204D). `Gained`/`Spent` are the pool's lifetime totals - the client subtracts to
	// get the balance, which is why a fresh character is told 0/0 rather than nothing at all.
	public sealed record UpdateTokenAmount(TokenPool TokenId, ulong Gained, ulong Spent, bool IncludeBonus);
}
