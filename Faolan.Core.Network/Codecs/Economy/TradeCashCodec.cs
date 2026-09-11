using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Economy
{
	/// <summary>
	///     The tin on the trade table: <c>[u64 tin]</c>, nothing else.
	/// </summary>
	/// <remarks>
	///     One body, two directions: the client's c2s <c>SetTradeCash</c> and the s2c <c>DispatchPartnerUpdatedCash</c>
	///     echo to the other party. The declared method is the s2c one - the message this codec sends - because the c2s
	///     twin is another interface's method index and cannot be spelled in the same enum.
	///     <para>
	///         Player-to-player trade, decoded from login_2-clients_group-trade-friend where both clients are on tape;
	///         byte-gated by <c>TeamTradeFriendCodecTests</c>.
	///     </para>
	/// </remarks>
	public static class TradeCashCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchPartnerUpdatedCash;

		// Canonical: the only method here that writes bytes.
		public static byte[] Build(TradeCash value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static TradeCash Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(TradeCashCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static TradeCash Read(ref SpanReader r)
		{
			return new TradeCash(r.ReadU64Be());
		}

		internal static void Write(FaolanStream s, TradeCash v)
		{
			s.WriteUInt64(v.Tin);
		}
	}

	/// <summary>The SetTradeCash / DispatchPartnerUpdatedCash body.</summary>
	public sealed record TradeCash(ulong Tin);
}
