using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Economy
{
	/// <summary>
	///     <c>DispatchUpdateCash</c> - the player's absolute wallet balance, not a delta:
	///     <c>[u64 balanceTin][u32 reason]</c>. The unit is tin, the game's smallest coin. Byte-exact in
	///     <c>UpdateCashTests</c>.
	/// </summary>
	public static class UpdateCashCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchUpdateCash;

		// Canonical: the only method here that writes bytes.
		public static byte[] Build(UpdateCash value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static UpdateCash Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(UpdateCashCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static UpdateCash Read(ref SpanReader r)
		{
			return new UpdateCash(r.ReadU64Be(), (UpdateCashReason)r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, UpdateCash v)
		{
			s.WriteUInt64(v.BalanceTin).WriteUInt32(v.Reason);
		}
	}

	/// <summary>The DispatchUpdateCash body.</summary>
	public sealed record UpdateCash(ulong BalanceTin, UpdateCashReason Reason);

	/// <summary>Why the wallet changed - the client celebrates a gain and stays quiet for a spend or sync.</summary>
	public enum UpdateCashReason : uint
	{
		Spend = 0, // also used for syncs that don't celebrate a gain
		Unknown2 = 2, // seen once in the captures; semantics unknown
		Gain = 3
	}
}
