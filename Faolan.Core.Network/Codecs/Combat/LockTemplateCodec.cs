using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Combat
{
	// GameCharInterface.DispatchLockTemplate (0x204E) - the SPELL COOLDOWN. This is what greys a spell's action-bar
	// button and runs its sweep; there is no recast GameAction on the wire at all (#767).
	//
	// Body, 8 bytes: [u32 spellId][f32 secondsRemaining] - the float BIG-endian, like every other raw-body float.
	//
	// Decoded over the corpus: 992 bodies, every one 8 bytes. Of the 741 whose spell id resolves against `Spells`,
	// **580 (78.3%) carry exactly `RecastCentiseconds / 100`** - the spell's own recast, in seconds. The rest are
	// partials against the same spell (15.143 / 15.254 / 15.76 where the recast is 20.0), i.e. the time LEFT rather
	// than the base, which is the same restore shape a long buff's duration takes (#601).
	//
	// ⚠️ Retail sends one per spell in the LINE, not one for the cast: a `Contract of Protection` cast emits five
	// bodies in a row - 195173 / 195178 / 195179 / 195180 / 1151248, every rank on the line - each carrying the same
	// 20.0. That is the wire form of `SetSpellLineRecastTime`, and it is why all five ranks of a spell grey together.
	//
	// The 251 bodies whose id is absent from `Spells` are ABILITIES and combos, which lock through the same message -
	// "template" is the client's word for the thing on the bar, not specifically a spell.
	public static class LockTemplateCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchLockTemplate;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(LockTemplate value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint templateId, float secondsRemaining)
		{
			return Build(new LockTemplate(templateId, secondsRemaining));
		}

		public static LockTemplate Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(LockTemplateCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static LockTemplate Read(ref SpanReader r)
		{
			return new LockTemplate(r.ReadU32Be(), r.ReadF32Be());
		}

		internal static void Write(FaolanStream s, LockTemplate v)
		{
			s.WriteUInt32(v.TemplateId).WriteFloat(v.SecondsRemaining);
		}
	}

	// The DispatchLockTemplate body: lock `TemplateId` (a spell, ability or combo) for `SecondsRemaining`.
	public sealed record LockTemplate(uint TemplateId, float SecondsRemaining);
}
