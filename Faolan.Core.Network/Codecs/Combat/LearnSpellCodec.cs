using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Combat
{
	// GameCharInterface.DispatchLearnSpell (0x200A) - the spell grant. The body is just the big-endian u32 spell id
	// (`i_nSpellID`); the client pops the "learned a new spell" toast itself. Decoded in
	// tools/capture/decode_learn_spell.py, byte-exact in LearnSpellTests.
	//
	// (The sibling DispatchForgetSpell 0x200B / RemoveSpecialAbility 0x2053 / UntrainSingleFeat 0x2011 have no capture
	// oracle yet, so they are not built - see #73.)
	public static class LearnSpellCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchLearnSpell;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(LearnSpell value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint spellId)
		{
			return Build(new LearnSpell(spellId));
		}

		public static LearnSpell Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(LearnSpellCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static LearnSpell Read(ref SpanReader r)
		{
			return new LearnSpell(r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, LearnSpell v)
		{
			s.WriteUInt32(v.SpellId);
		}
	}

	// The DispatchLearnSpell body.
	public sealed record LearnSpell(uint SpellId);
}
