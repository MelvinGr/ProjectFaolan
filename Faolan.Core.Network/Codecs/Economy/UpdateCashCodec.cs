using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Economy
{
	// DispatchUpdateCash (s2c 0x2005) - the player's ABSOLUTE wallet balance, not a delta:
	//   [u64 balanceTin][u32 reason]
	// The unit is TIN, the game's smallest coin (100 tin = 1 copper, ...). Byte-exact in UpdateCashTests; was
	// `UpdateCashBody.WriteUpdateCashBody(FaolanStream, …)` (#530 wave 7).
	public static class UpdateCashCodec
	{
		public const ushort Opcode = (ushort)GameCharInterfaceOpcodes.DispatchUpdateCash;

		// CANONICAL - the only method here that writes bytes.
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

	// DispatchUpdateCash (0x2005).
	public sealed record UpdateCash(ulong BalanceTin, UpdateCashReason Reason);

	// Why the wallet changed - the client celebrates a Gain and stays quiet for a Spend/sync.
	public enum UpdateCashReason : uint
	{
		Spend = 0, // also used for syncs that don't celebrate a gain
		Unknown2 = 2, // seen once in the captures; semantics unknown
		Gain = 3
	}
}
