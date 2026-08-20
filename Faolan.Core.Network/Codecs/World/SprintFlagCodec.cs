using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.World
{
	// DispatchSetIsSprinting(isSprinting) / DispatchSyncCanSprint(canSprint) / DispatchSyncRegenDelayed(regenDelayed) -
	// GameCharInterfaceProject 0x0B / 0x0C / 0x0D. Three server-owned booleans about the sprint pool, each one byte,
	// each addressed to the character it is about through the receiver endpoint (#867). The verb is in the method and
	// the layout is identical, so they share one codec (`HandledMethods`, #530).
	//
	// The client names them itself: `DispatchSetIsSprinting(isSprinting)`, `DispatchSyncCanSprint(canSprint)`,
	// `DispatchSyncRegenDelayed(regenDelayed)` (docs/re/aoc-client-rpc-methods.json).
	//
	// Corpus (`--report sprint`, 134 captures): `isSprinting` is 00 x1314 / 01 x1310 - a near-perfect split, as an
	// on-off toggle must be. `canSprint` is 01 x325 / 00 x151. `regenDelayed` is **00 in all 1,226 frames**: every
	// captured send CLEARS the delay, so the corpus proves the transport and not the set condition.
	// `[V0: what sets regenDelayed - no capture carries a 1]`
	//
	// ⚠️ The body carries NO entity. Which character a flag is about is decided by the RPC receiver endpoint's
	// instance, and in all 65 captures that carry `isSprinting` it is one instance per capture - the session's own
	// character. So this is the server's authoritative echo to the player, not a broadcast about anybody else.
	public static class SprintFlagCodec
	{
		public static readonly GameCharInterfaceProjectMethods[] HandledMethods =
		[
			GameCharInterfaceProjectMethods.DispatchSetIsSprinting,
			GameCharInterfaceProjectMethods.DispatchSyncCanSprint,
			GameCharInterfaceProjectMethods.DispatchSyncRegenDelayed
		];

		public static byte[] Build(SprintFlag value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static byte[] Build(bool on)
		{
			return Build(new SprintFlag(on));
		}

		public static SprintFlag Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(SprintFlagCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static SprintFlag Read(ref SpanReader r)
		{
			return new SprintFlag(r.ReadU8() != 0);
		}

		internal static void Write(FaolanStream s, SprintFlag v)
		{
			s.WriteByte(v.On ? (byte)0x01 : (byte)0x00);
		}
	}

	public sealed record SprintFlag(bool On);
}
