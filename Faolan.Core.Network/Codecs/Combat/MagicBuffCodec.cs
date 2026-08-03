using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// MagicBuffIIR (0x6B87E316) - SPELL APPLICATION on an entity. `docs/tooling/capture-events.md` calls it
	// "ApplySpell (cast broadcast)", and it is the only message that names the spell involved, which makes it the
	// one that turns a combat capture from handles-and-numbers into "Flesh to Worms (Rank 1)".
	//
	//   [subop][class][applyTo][u8 env][u32 spellBaseId][u8 form][class][applyFrom][data]
	//
	// The SHORT form ends there (26 B content); the APPLY form carries a 54-byte `data` payload
	// (`MagicBuffDataCodec`). Which one a record is comes from `Form`, the low byte of the wire spell id, and it is the
	// client's own selector rather than a rank (#438): MagicBuffIIR_c::ReadSubClass switches on it three ways -
	// 0 = APPLY, 1 = END (nothing more), 2 = UPDATE (which retail never sends). The same base ids appear under both 0
	// and 1, which is a start/stop pair rather than two ranks of one spell.
	//
	// ⚠️ `SpellBaseId` + `Form` ARE the wire spell id, split. That split is what merged the two emitters this codec
	// absorbed (#530 wave 4): one wrote `[u8 env][u32 spellBaseId][u8 form]` and the other `[u16 0][u32 wireSpellId]`,
	// six bytes each, and they are byte-identical because wireSpellId = (baseId << 8) | form and a base id never uses
	// its high byte. Two vocabularies, one record. `MagicBuffIds` converts between them.
	//
	// ⚠️ The message does NOT say which of cast / refresh / login-replay it is. The whole standing buff set is re-sent
	// at world entry through this same message, so anything that calls it "casts" is inventing a distinction the bytes
	// do not carry.
	//
	// `Data` is carried as bytes rather than composed: `MagicBuffDataCodec` decodes the 54-byte APPLY payload, but the
	// END form carries none and a `Form` we have not seen could carry something else, so the parent stays total by
	// keeping the region opaque and letting the caller decode it when it wants the fields.
	//
	// [V2: xxx_mage-necromancer_login_play2_tortage_city_first-quests.tshark.json - 155 bodies, 76 short / 79 long,
	// every spell id resolving to a Spells row]
	public static class MagicBuffCodec
	{
		public const uint Opcode = (uint)IirTypes.MagicBuffIIR;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(MagicBuffIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		// The form a caller that holds the WIRE spell id wants: the id splits into the base id and the form selector.
		public static byte[] Build(uint applyTo, uint applyFrom, uint wireSpellId, byte[]? data = null)
		{
			return Build(new MagicBuffIIR(applyTo, 0x00, MagicBuffIds.BaseOf(wireSpellId), MagicBuffIds.FormOf(wireSpellId), applyFrom, data ?? []));
		}

		// The TYPED apply: the parent composes its own data region rather than being handed 54 pre-built bytes.
		// `null` is the END twin, which carries no data at all - that is the whole reason `MagicBuffIIR.Data` stays
		// opaque, so the choice lives here in the overload rather than in the value (#530 rule 5b / #539).
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
			// applyFrom is an OPTIONAL entity reference. Retail writes a zero class word when the buff has no caster,
			// and then a zero instance behind it: 14 of the 4,666 captured MagicBuff bodies do this, across three
			// captures. They are the same grammar, not a misread - their body lengths are exactly the two normal ones
			// (26 short / 80 long), which a shifted read could not produce. Requiring the class here is what aborted a
			// full import (#614).
			var applyFromClass = r.ReadU32Be();
			if (applyFromClass != EntityClass.Character && applyFromClass != 0)
				throw new CodecParseException($"MagicBuffCodec: expected applyFrom class 0x{EntityClass.Character:X8} or 0 (no caster), got 0x{applyFromClass:X8}");

			var applyFrom = r.ReadU32Be();

			// A null reference is BOTH words zero. A zero class with a non-zero instance is neither a Character nor
			// "no caster", so it stays an error rather than being read as one.
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

	// MagicBuffIIR (0x6B87E316). `Form` is the wire spell id's low byte: 0 APPLY, 1 END, 2 UPDATE.
	//
	// `ApplyFrom` is NULL when the buff has no caster - the wire writes a zero class word where the entity class would
	// be, and a zero instance behind it (see Read). Not a sentinel to be flattened to 0: an unsourced buff and a buff
	// cast by entity 0 would be the same value, and the whole point of the field is which entity to attribute it to.
	public sealed record MagicBuffIIR(uint ApplyTo, byte Env, uint SpellBaseId, byte Form, uint? ApplyFrom, byte[] Data);

	// The wire spell id <-> (base id, form) split. Not a codec - it is arithmetic on one field, named so the shift and
	// the mask stop being written out at each call site.
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
