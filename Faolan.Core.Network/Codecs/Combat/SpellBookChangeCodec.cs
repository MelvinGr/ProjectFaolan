using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Combat
{
	// GameCharInterface.DispatchLearnSpell (0x200A) / DispatchForgetSpell (0x200B) - one spell entering or leaving the
	// client's spellbook. The body is just the big-endian u32 spell id (`i_nSpellID`); the client pops the "learned a
	// new spell" toast itself on the grant. Decoded in tools/capture/decode_learn_spell.py, byte-exact in
	// SpellBookChangeTests.
	//
	//   [u32 i_nSpellID]
	//
	// ONE body, TWO methods with the verb in the method, so they share a codec (`HandledMethods`, #530) rather than
	// one of them claiming to be *the* `Method`.
	//
	// ⭐ The FORGET half has no capture oracle and does not need one (#915). Both dispatch readers are in the client
	// and they are the same function twice: `FUN_004bf2b0` (learn) and `FUN_004bf3a0` (forget) each make a single call
	// to `FUN_0048f900` - a 4-byte stream read that byte-swaps to host order, i.e. one big-endian u32 - and then hand
	// the value to adjacent vtable slots (+0x2c and +0x30). Their "stream bad after 'i_nSpellID'" diagnostics name the
	// same single field. So the forget body is the learn body; there is nothing left for a capture to decide.
	// [V1: docs/re/aoc-client-rpc-methods.json]
	public static class SpellBookChangeCodec
	{
		public static readonly GameCharInterfaceMethods[] HandledMethods =
			[GameCharInterfaceMethods.DispatchLearnSpell, GameCharInterfaceMethods.DispatchForgetSpell];

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(SpellBookChange value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint spellId)
		{
			return Build(new SpellBookChange(spellId));
		}

		public static SpellBookChange Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(SpellBookChangeCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static SpellBookChange Read(ref SpanReader r)
		{
			return new SpellBookChange(r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, SpellBookChange v)
		{
			s.WriteUInt32(v.SpellId);
		}
	}

	// The DispatchLearnSpell / DispatchForgetSpell body.
	public sealed record SpellBookChange(uint SpellId);
}
