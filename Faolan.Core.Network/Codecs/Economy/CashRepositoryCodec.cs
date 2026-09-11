using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Economy
{
	/// <summary>
	///     <c>DispatchUpdateCashRepository(newCash, feedback)</c> - the bank's cash line, as distinct from the wallet the
	///     money packet moves: <c>[u64 newCash][u32 feedback]</c>.
	/// </summary>
	/// <remarks>
	///     Twelve bytes, which is what the single captured frame carries; the two fields adding to 12 is what pins the
	///     widths, since that frame is all zeros and offers no variance. A u64 for money matches every other money field
	///     on this wire, tin being a 64-bit quantity.
	///     <para>
	///         <c>feedback</c> is the same shape the mail operations use for their result code - zero meaning "fine" and
	///         a non-zero value naming a refusal. [V0: the feedback codes for a cash-repository update]
	///     </para>
	/// </remarks>
	public static class CashRepositoryCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchUpdateCashRepository;

		public static byte[] Build(CashRepository value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static byte[] Build(ulong newCash, uint feedback = 0)
		{
			return Build(new CashRepository(newCash, feedback));
		}

		public static CashRepository Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(CashRepositoryCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static CashRepository Read(ref SpanReader r)
		{
			var newCash = r.ReadU64Be();

			return new CashRepository(newCash, r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, CashRepository v)
		{
			s.WriteUInt64(v.NewCash).WriteUInt32(v.Feedback);
		}
	}

	public sealed record CashRepository(ulong NewCash, uint Feedback);
}
