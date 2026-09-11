using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     MagicBuffIIR - spell application on an entity, what docs/tooling/capture-events.md calls the cast broadcast. It
	///     is the only message that names the spell involved, which is what turns a combat capture from handles and numbers
	///     into named spells. Layout:
	///     <c>[subop][class][applyTo][u8 env][u32 spellBaseId][u8 form][class][applyFrom][data]</c>.
	/// </summary>
	/// <remarks>
	///     The short form ends there, 26 bytes of content; the apply form carries a 54-byte data payload (see
	///     <see cref="MagicBuffDataCodec" />). Which one a record is comes from <c>Form</c>, the low byte of the wire spell
	///     id, and it is the client's own selector rather than a rank: MagicBuffIIR_c::ReadSubClass switches on it three
	///     ways - 0 apply, 1 end (nothing more), 2 update (which retail never sends). The same base ids appear under both 0
	///     and 1, so it is a start/stop pair rather than two ranks of one spell.
	///     <para>
	///         <c>SpellBaseId</c> and <c>Form</c> together are the wire spell id, split: wireSpellId = (baseId &lt;&lt; 8) |
	///         form, and a base id never uses its high byte. <see cref="MagicBuffIds" /> converts between the two forms.
	///     </para>
	///     <para>
	///         The message does not say which of cast, refresh or login-replay it is. The whole standing buff set is re-sent
	///         at world entry through this same message, so treating it as "a cast" invents a distinction the bytes do not
	///         carry.
	///     </para>
	///     <para>
	///         <c>Data</c> is carried as bytes rather than composed: the end form carries none and an unseen <c>Form</c>
	///         could carry something else, so the parent stays total by keeping the region opaque and letting the caller
	///         decode it when it wants the fields.
	///     </para>
	///     [V2: xxx_mage-necromancer_login_play2_tortage_city_first-quests.tshark.json - 155 bodies, 76 short / 79 long,
	///     every spell id resolving to a Spells row]
	/// </remarks>
	public static class MagicBuffCodec
	{
		public const uint Opcode = (uint)IirTypes.MagicBuffIIR;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
		public static byte[] Build(MagicBuffIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		/// <summary>
		///     The form a caller holding the wire spell id wants: the id splits into the base id and the form selector.
		/// </summary>
		public static byte[] Build(uint applyTo, uint applyFrom, uint wireSpellId, byte[]? data = null)
		{
			return Build(new MagicBuffIIR(applyTo, 0x00, MagicBuffIds.BaseOf(wireSpellId), MagicBuffIds.FormOf(wireSpellId), applyFrom, data ?? []));
		}

		/// <summary>
		///     The typed apply: the parent composes its own data region rather than being handed 54 pre-built bytes. A null
		///     <paramref name="data" /> is the end twin, which carries no data at all - which is why the choice lives in this
		///     overload rather than in the value.
		/// </summary>
		public static byte[] Build(uint applyTo, uint applyFrom, uint wireSpellId, MagicBuffData? data)
		{
			return Build(applyTo, applyFrom, wireSpellId, data == null
				? null
				: MagicBuffDataCodec.Build(data));
		}

		public static MagicBuffIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(MagicBuffCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static MagicBuffIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "MagicBuffIIR subop");
			r.ExpectU32Be(EntityClass.Character, "applyTo class");
			var applyTo = r.ReadU32Be();
			var env = r.ReadU8();
			var spellBaseId = r.ReadU32Be();
			var form = r.ReadU8();
			// applyFrom is an optional entity reference: retail writes a zero class word when the buff has no caster,
			// and a zero instance behind it. 14 of the 4,666 captured MagicBuff bodies do this, across three captures,
			// and their body lengths are exactly the two normal ones (26 short / 80 long), which a shifted read could
			// not produce. The class must therefore not be required.
			var applyFromClass = r.ReadU32Be();
			if (applyFromClass != EntityClass.Character && applyFromClass != 0)
				throw new CodecParseException($"MagicBuffCodec: expected applyFrom class 0x{EntityClass.Character:X8} or 0 (no caster), got 0x{applyFromClass:X8}");

			var applyFrom = r.ReadU32Be();

			// A null reference is both words zero. A zero class with a non-zero instance is neither a Character nor a
			// missing caster, so it stays an error rather than being read as one.
			if (applyFromClass == 0 && applyFrom != 0)
				throw new CodecParseException($"MagicBuffCodec: applyFrom class is 0 (no caster) but the instance is 0x{applyFrom:X8}");

			return new MagicBuffIIR(applyTo, env, spellBaseId, form, applyFromClass == 0 ? null : applyFrom, r.ReadRemaining().ToArray());
		}

		internal static void Write(FaolanStream s, MagicBuffIIR v)
		{
			s
				.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(v.ApplyTo)
				.WriteByte(v.Env)
				.WriteUInt32(v.SpellBaseId)
				.WriteByte(v.Form)
				.WriteUInt32(v.ApplyFrom is null ? 0 : EntityClass.Character)
				.WriteUInt32(v.ApplyFrom ?? 0)
				.WriteArray(v.Data);
		}
	}

	/// <summary>
	///     The MagicBuffIIR body. <c>Form</c> is the wire spell id's low byte: 0 apply, 1 end, 2 update.
	/// </summary>
	/// <remarks>
	///     <c>ApplyFrom</c> is null when the buff has no caster - the wire writes a zero class word where the entity class
	///     would be, and a zero instance behind it. It must not be flattened to 0: an unsourced buff and a buff cast by
	///     entity 0 would then be the same value, and the field exists to say which entity to attribute the buff to.
	/// </remarks>
	public sealed record MagicBuffIIR(uint ApplyTo, byte Env, uint SpellBaseId, byte Form, uint? ApplyFrom, byte[] Data);

	/// <summary>
	///     The wire spell id to (base id, form) split. Not a codec but arithmetic on one field, named so the shift and the
	///     mask are not written out at each call site.
	/// </summary>
	public static class MagicBuffIds
	{
		public const byte FormApply = 0;
		public const byte FormEnd = 1;

		public static uint BaseOf(uint wireSpellId)
		{
			return wireSpellId >> 8;
		}

		public static byte FormOf(uint wireSpellId)
		{
			return (byte)wireSpellId;
		}

		public static uint WireIdOf(uint spellBaseId, byte form)
		{
			return (spellBaseId << 8) | form;
		}
	}
}
