using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     GameCharInterface.DispatchLearnSpell / DispatchForgetSpell - one spell entering or leaving the client's
	///     spellbook. The body is a single big-endian u32 spell id (<c>i_nSpellID</c>); the client pops the "learned a new
	///     spell" toast itself on the grant. Decoded in tools/capture/decode_learn_spell.py, byte-exact in
	///     SpellBookChangeTests.
	/// </summary>
	/// <remarks>
	///     One body, two methods with the verb in the method, so they share a codec through <see cref="HandledMethods" />
	///     rather than one of them claiming to be the method.
	///     <para>
	///         The forget half has no capture oracle and does not need one: both dispatch readers are the same function
	///         twice. FUN_004bf2b0 (learn) and FUN_004bf3a0 (forget) each make a single call to FUN_0048f900 - a 4-byte
	///         stream read byte-swapped to host order - and hand the value to adjacent vtable slots (+0x2c and +0x30), and
	///         both "stream bad after 'i_nSpellID'" diagnostics name the same single field.
	///         [V1: docs/re/aoc-client-rpc-methods.json]
	///     </para>
	/// </remarks>
	public static class SpellBookChangeCodec
	{
		public static readonly GameCharInterfaceMethods[] HandledMethods =
			[GameCharInterfaceMethods.DispatchLearnSpell, GameCharInterfaceMethods.DispatchForgetSpell];

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
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

	/// <summary>The DispatchLearnSpell / DispatchForgetSpell body.</summary>
	public sealed record SpellBookChange(uint SpellId);
}
