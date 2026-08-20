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
	// WHICH pool a tokenId names is still open (Gitea #870): the send is event-driven and the corpus shows four
	// different ids - 1 at character creation/first world entry with 0/0, 4 on a Destiny quest completion (running
	// total 1 then 2 for the same character), 32 in a group-finder session and 50 on a login-reward claim.
	// The server never sends this today; the codec exists so the decode is written down where the next caller
	// looks.
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
			return new UpdateTokenAmount(r.ReadU8(), r.ReadU64Be(), r.ReadU64Be(), r.ReadU8() != 0);
		}

		internal static void Write(FaolanStream s, UpdateTokenAmount v)
		{
			s.WriteByte(v.TokenId).WriteUInt64(v.Gained).WriteUInt64(v.Spent).WriteByte((byte)(v.IncludeBonus ? 1 : 0));
		}
	}

	// DispatchUpdateTokenAmount (0x204D). `Gained`/`Spent` are the pool's lifetime totals - the client subtracts to
	// get the balance, which is why a fresh character is told 0/0 rather than nothing at all.
	public sealed record UpdateTokenAmount(byte TokenId, ulong Gained, ulong Spent, bool IncludeBonus);
}
