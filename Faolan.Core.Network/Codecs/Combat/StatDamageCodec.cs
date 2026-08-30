using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// StatDamageIIR (0xE9F24184) - a signed change to one of the target's stats, WITH its cause. It is what renders
	// spell and damage-over-time numbers where DamageResultIIR renders the melee hit (#349), what the client's draining
	// spellweave gauges tick on, and what the world uses to hurt a player who is not being attacked by anybody.
	//
	// The carrier, then seventeen big-endian words - or ELEVEN, and the carrier's own entity CLASS is what says which.
	//
	//   [subop][class][handle][u8 env]
	//   [statValue][0][0][deathReason][statId][count]   <- these six ONLY when class != 0xC879
	//   [i32 delta][school]
	//   [casterClass][caster][idx][0][spellBaseId][magnitude][0][0][0]
	//
	// ⭐ WHAT THE CLIENT DOES WITH IT (`StatDamageIIR_t` vtable slot 2, `FUN_008d5960`, #902). Reading the apply is
	// what settles which of these seventeen words matter, and the answer is not the one the field names suggest:
	//
	//   * `delta` (@0x30) is the ONLY thing that moves a stat: `SetStat(statId, GetStat(statId) + delta)`, run
	//     unconditionally once the carrier resolves to a `CoreCharacter` (or, on the 0xC879 branch, a `Destructible`,
	//     where it SUBTRACTS - that is the harvest node chipping down).
	//   * `statValue` (@0x2c) is **never read**. The client recomputes from what it already holds. It is still stated
	//     because retail states it, and because it is the only field that pins the delta's SCALE: it equals the same
	//     handle's `StatUpdateIIR` stat 27 exactly (diff 0 on twelve handles, up to 92 records each) and moves by
	//     exactly `delta` across 1,015 consecutive pairs. Stat 27 is ×100, so both are ×100.
	//   * `spellBaseId` (@0x54) is read in ONE place - `if (deathReason == 10) FUN_007e95a0(spellBaseId)` - and
	//     nothing downstream depends on it. ⛔ A record naming spell 0 is NOT voided; #902 assumed it was, and the
	//     `GetMagicSpell failed to load spell with id 0` in the client log comes from one of ~80 other callers.
	//   * `deathReason` (@0x40) is not filler. It goes out as `SetStat(0x152, deathReason)` - stat 0x152 is
	//     `CharacterStatId.DeathReason` - it gates that spell lookup at 10, and when non-zero it drives a virtual call
	//     on the target (`vtable +0x278`). The corpus carries 0 (×2,201), 5 (×127) and 10 (×52) on health records.
	//   * `idx` (@0x50) non-zero selects the richer combat-log/floating-number path over the plain one, and `count`
	//     (@0x74) tweaks its verb at 5 and 6. Neither touches the stat.
	//
	// ⚠️ The reader SCATTERS the seven-word tail rather than storing it in order (`FUN_00897b60`): wire positions
	// 1..7 land at +0x50, +0x58, +0x54, +0x5c, +0x70, +0x60, +0x64 - which is why `spellBaseId` is the THIRD tail
	// word on the wire and `Idx` the first. Getting that backwards puts the spell id in a word nothing reads.
	//
	// ⚠️ The class word was asserted to be 0xC350 Character until #811, which refused all 13 records carrying 0xC879
	// (`EntityClass.Destructible`) - the player chipping at a harvest node or a breakable. The client says the branch
	// outright: `StatDamageIIR_t::ReadSubClass` (FUN_008D57C0) opens with
	//     if (this->carrier->type != 0xc879) { <six reads> }
	// and only then reads delta, school and the caster pair. So it is not a shorter message that happens to fit - the
	// six leading words are conditional on the target being a CHARACTER, which is the only kind of target that has a
	// stat sheet for them to describe. The 13 captured destructible records fit exactly: 57 bytes = 13 + 11 words,
	// delta 4676..17606 with school 7 and 10 alternating, caster = the player, and every trailing word zero.
	//
	// THREE emitters wrote those seventeen words under three different names before #530 wave 4 merged them, and the
	// merge is the point of the rule: they are the same record, and reading them side by side is what shows it.
	//
	//   * COMBAT (`StatDamages.Combat`) - a spell/DoT tick. statValue = the target's resulting health, delta the signed
	//     change, spellBaseId the BASE spell id (wire id >> 8, e.g. Rend 0x316449), idx the effect's slot (1, 2, ...).
	//     Golden: fr 6689 = Rend ticking the trapper's victim for 8 Slashing.
	//   * ENVIRONMENTAL (`StatDamages.Environmental`) - what retail sends when the WORLD damages a player rather than a
	//     creature; all 28 fall events in the corpus (#495/#504). statValue is the damage MAGNITUDE here, not a
	//     resulting health, and delta is exactly its negation - proven by consecutive falls: chat-annotated-3 carries
	//     50994 -> 14076 -> 219125 seven seconds apart, which cannot be a health track. There is no caster (the world
	//     is not an entity), so casterClass, caster, idx, spellBaseId and magnitude are all 0 and count is 0 rather
	//     than 2. The school is 12, outside the 0..10 the combat schools occupy.
	//     ⚠️ No DamageResultIIR accompanies it - a fall renders no floating combat number.
	//   * GAUGE (`StatDamages.SpellweaveGauge`) - the client's draining spellweave bar. statValue is the current bar
	//     value, statId the gauge's per-type constant (v@33), spellBaseId the gauge id, magnitude 80.
	//
	// ⚠️ The word after `delta` is the damage SCHOOL, not a stat id - it used to be called StatId, which made the
	// golden read as "Rend reduces stat 1 by 8" instead of "Rend deals 8 Slashing". Every value the corpus carries is
	// 0..10, exactly the DamageSchool range (`--report npc-damage`).
	//
	// The u32 carrier length belongs to the SENDER (#530 rule 2).
	public static class StatDamageCodec
	{
		public const uint Opcode = (uint)IirTypes.StatDamageIIR;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(StatDamageIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static StatDamageIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(StatDamageCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static StatDamageIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "StatDamageIIR subop");
			var entityClass = r.ReadU32Be();
			var handle = r.ReadU32Be();
			var env = r.ReadU8();

			// A destructible target has no stat sheet, so the six words describing one are simply not on the wire.
			var describesAStat = entityClass != EntityClass.Destructible;
			var statValue = describesAStat ? r.ReadU32Be() : 0;
			var word1 = describesAStat ? r.ReadU32Be() : 0;
			var word2 = describesAStat ? r.ReadU32Be() : 0;
			var deathReason = describesAStat ? r.ReadU32Be() : 0;
			var statId = describesAStat ? r.ReadU32Be() : 0;
			var count = describesAStat ? r.ReadU32Be() : 0;
			var delta = r.ReadI32Be();
			var school = (DamageSchool)r.ReadU32Be();
			var casterClass = r.ReadU32Be();
			var caster = r.ReadU32Be();
			var idx = r.ReadU32Be();
			var word11 = r.ReadU32Be();
			var spellBaseId = r.ReadU32Be();
			var magnitude = r.ReadU32Be();

			return new StatDamageIIR(handle, env, statValue, statId, count, delta, school, casterClass, caster, idx, spellBaseId, magnitude, word1, word2, deathReason, word11, r.ReadU32Be(),
				r.ReadU32Be(), r.ReadU32Be(), entityClass);
		}

		internal static void Write(FaolanStream s, StatDamageIIR v)
		{
			s
				.WriteUInt32(Opcode)
				.WriteUInt32(v.EntityClass)
				.WriteUInt32(v.Handle)
				.WriteByte(v.Env);

			if (v.EntityClass != EntityClass.Destructible)
				s
					.WriteUInt32(v.StatValue)
					.WriteUInt32(v.Word1)
					.WriteUInt32(v.Word2)
					.WriteUInt32(v.DeathReason)
					.WriteUInt32(v.StatId)
					.WriteUInt32(v.Count);

			s
				.WriteUInt32(v.Delta)
				.WriteUInt32(v.School)
				.WriteUInt32(v.CasterClass)
				.WriteUInt32(v.Caster)
				.WriteUInt32(v.Idx)
				.WriteUInt32(v.Word11)
				.WriteUInt32(v.SpellBaseId)
				.WriteUInt32(v.Magnitude)
				.WriteUInt32(v.Word14)
				.WriteUInt32(v.Word15)
				.WriteUInt32(v.Word16);
		}
	}

	// StatDamageIIR (0xE9F24184). The seventeen words in wire order, with the ones still undecoded carried as `Word*`
	// fields rather than asserted - a record with something in them round-trips instead of being rejected, which is
	// what an undecoded word deserves.
	//
	// ⚠️ "0 in every captured record" is what this used to say of all six, and it was never measured on the HEALTH
	// population: `DeathReason` carries 5 and 10 in 179 of 2,380 health records and `Word1` carries 12/5/1 in 294 of
	// them (#902). Which is exactly why they are carried rather than asserted - but it also meant a word the client
	// acts on was filed as padding.
	public sealed record StatDamageIIR(
		uint Handle,
		byte Env,

		// ⚠️ NOT the stat's value after the hit, whatever the name suggests. On the ENVIRONMENTAL form it is the
		// damage MAGNITUDE - the same number `Delta` carries, negated: 41 of the 1,884 negative-health records in
		// the corpus have `StatValue == -Delta`, and the environmental population is exactly those 41. On the 1,843
		// combat ones it is something else.
		//
		// ⭐ Worth knowing because it looks like a free health pool. Reading a fall's `before` as
		// `StatValue + damage` makes every fall cost exactly 50% of it, which is arithmetic rather than a finding
		// (#515) - the health pool for a fall genuinely has to come from the separate `StatUpdateIIR` stream.
		uint StatValue,
		uint StatId,
		uint Count,
		int Delta,
		DamageSchool School,
		uint CasterClass,
		uint Caster,
		uint Idx,
		uint SpellBaseId,
		uint Magnitude,
		uint Word1 = 0,
		uint Word2 = 0,

		// The client writes this straight to stat 0x152 - `CharacterStatId.DeathReason` - so it is the CAUSE word, not
		// a filler: what killed you, stated on every hit that might. It also gates the record's only spell lookup
		// (`== 10`) and, when non-zero, a virtual call on the target. Corpus values on a health record: 0, 5, 10.
		uint DeathReason = 0,
		uint Word11 = 0,
		uint Word14 = 0,
		uint Word15 = 0,
		uint Word16 = 0,

		// The carrier's entity class, and a DISCRIMINATOR rather than a label: 0xC879 (`EntityClass.Destructible`)
		// means the six stat-sheet words ahead of `Delta` are not on the wire at all - see the codec's note.
		uint EntityClass = Constants.EntityClass.Character);

	// The three semantic forms of a StatDamageIIR, as VALUE factories.
	//
	// Factories rather than `Build` overloads because the forms collide on signature - a combat tick and an
	// environmental hit are both "a handle, three uints and a school" - so choosing between them by parameter order is
	// exactly the ambiguity #530 rule 1 removes. Same treatment `MoveTaskNodes` got in wave 3.
	public static class StatDamages
	{
		// The 0x1B marker every combat tick carries in the statId slot. It is stat 0x1B = HealthCurrent, the stat the
		// tick moves.
		public const uint HealthStatId = 0x1B;

		// The spellweave gauges (docs/subsystems/combos.md, decoded in tools/capture/decode_spellweave.py from
		// NecroL80_..._consuming-potions-food-spellweaving; caster 0x0802E5D4). Two are used: the STAMINA gauge that
		// drains while weaving, and, once stamina empties, the health DoT gauge that damages the caster.
		public const uint SpellweaveDotGauge = 0x37E690; // "Spellweaving" -> the health DoT when stamina is empty
		public const uint SpellweaveDotScale = 0x0000001B; // v@33 for the DoT gauge
		public const uint SpellweaveStaminaGauge = 0x37E68C; // "Spellweaving Cost" -> the yellow stamina bar
		public const uint SpellweaveStaminaScale = 0x000F425C; // v@33 for the stamina gauge

		// The count word both entity-caused forms carry.
		private const uint CasterCount = 2;

		private const uint SpellweaveGaugeMagnitude = 80;

		// A spell / damage-over-time tick from `caster` on `target`.
		public static StatDamageIIR Combat(uint target, uint statValue, int delta, DamageSchool school, uint caster, uint idx, uint spellBaseId)
		{
			return new StatDamageIIR(target, 0x00, statValue, HealthStatId, CasterCount, delta, school, EntityClass.Character, caster, idx, spellBaseId, 1);
		}

		// The WORLD damaging `target` - fall damage and the like. No caster, count 0, and the delta is the negation of
		// the amount.
		public static StatDamageIIR Environmental(uint target, uint amount, uint statId, uint school)
		{
			return new StatDamageIIR(target, 0x00, amount, statId, 0, -(int)amount, (DamageSchool)school, 0, 0, 0, 0, 0);
		}

		// One tick of a draining spellweave gauge. `token` is the current bar value (drains toward 0), `delta` the
		// signed change since the last tick; `scale` is the gauge's per-type constant (v@33). Byte-exact vs frames 1654
		// (stamina) and the DoT frame (0x37E690) of the cancel-weave/DoT capture.
		public static StatDamageIIR SpellweaveGauge(uint caster, uint token, int delta, uint gaugeId, uint scale)
		{
			return new StatDamageIIR(caster, 0x00, token, scale, CasterCount, delta, (DamageSchool)0, EntityClass.Character, caster, 1, gaugeId, SpellweaveGaugeMagnitude);
		}
	}
}
