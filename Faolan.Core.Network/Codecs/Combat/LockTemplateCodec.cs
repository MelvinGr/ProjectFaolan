using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     GameCharInterface.DispatchLockTemplate - the spell cooldown. It greys a spell's action-bar button and runs its
	///     sweep; there is no recast GameAction on the wire at all. The body is 8 bytes,
	///     <c>[u32 templateId][f32 secondsRemaining]</c>, the float big-endian like every other raw-body float.
	/// </summary>
	/// <remarks>
	///     Decoded over the corpus: 992 bodies, every one 8 bytes. Of the 741 whose id resolves against Spells, 580 (78.3%)
	///     carry exactly RecastCentiseconds / 100 - the spell's own recast in seconds. The rest are partials against the same
	///     spell (15.143 / 15.254 / 15.76 where the recast is 20.0), i.e. the time left rather than the base, the same
	///     restore shape a long buff's duration takes.
	///     <para>
	///         Retail sends one body per spell in the line rather than one for the cast: a Contract of Protection cast emits
	///         five bodies in a row - 195173 / 195178 / 195179 / 195180 / 1151248, every rank on the line - each carrying the
	///         same 20.0. That is the wire form of SetSpellLineRecastTime, and why all five ranks grey together.
	///     </para>
	///     <para>
	///         The 251 bodies whose id is absent from Spells are abilities and combos, which lock through the same message:
	///         "template" is the client's word for the thing on the bar, not specifically a spell.
	///     </para>
	/// </remarks>
	public static class LockTemplateCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchLockTemplate;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
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

	/// <summary>
	///     The DispatchLockTemplate body: lock <c>TemplateId</c> - a spell, ability or combo - for
	///     <c>SecondsRemaining</c>.
	/// </summary>
	public sealed record LockTemplate(uint TemplateId, float SecondsRemaining);
}
