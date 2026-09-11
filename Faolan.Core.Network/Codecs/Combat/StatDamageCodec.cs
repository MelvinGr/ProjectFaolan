using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     StatDamageIIR - a signed change to one of the target's stats, with its cause. It renders spell and
	///     damage-over-time numbers where DamageResultIIR renders the melee hit, drives the client's draining spellweave
	///     gauges, and is how the world hurts a player nobody is attacking.
	/// </summary>
	/// <remarks>
	///     The carrier, then seventeen big-endian words - or eleven, the carrier's own entity class deciding which:
	///     <code>
	///     [subop][class][handle][u8 env]
	///     [statValue][0][0][deathReason][statId][count]   these six only when the class is not Destructible
	///     [i32 delta][school]
	///     [casterClass][caster][idx][0][spellBaseId][magnitude][0][0][0]
	///     </code>
	///     <para>
	///         What the client does with it (StatDamageIIR_t vtable slot 2, FUN_008d5960) settles which words matter:
	///         <list type="bullet">
	///             <item>
	///                 <c>delta</c> (@0x30) is the only thing that moves a stat - <c>SetStat(statId, GetStat(statId) +
	///                 delta)</c>, run unconditionally once the carrier resolves to a CoreCharacter, or on the Destructible
	///                 branch to a Destructible, where it subtracts instead: that is the harvest node chipping down.
	///             </item>
	///             <item>
	///                 <c>statValue</c> (@0x2c) is never read - the client recomputes from what it holds. It is still stated
	///                 because retail states it, and because it is the only field that pins the delta's scale: it equals the
	///                 same handle's StatUpdateIIR stat 27 exactly (no difference over twelve handles, up to 92 records each)
	///                 and moves by exactly <c>delta</c> across 1,015 consecutive pairs. Stat 27 is x100, so both are x100.
	///             </item>
	///             <item>
	///                 <c>spellBaseId</c> (@0x54) is read in one place, <c>if (deathReason == 10) FUN_007e95a0(spellBaseId)</c>,
	///                 and nothing downstream depends on it. A record naming spell 0 is not voided.
	///             </item>
	///             <item>
	///                 <c>deathReason</c> (@0x40) is the cause word, not filler: it goes out as <c>SetStat(0x152,
	///                 deathReason)</c> - stat 0x152 is CharacterStatId.DeathReason - gates the spell lookup at 10, and when
	///                 non-zero drives a virtual call on the target (vtable +0x278). The corpus carries 0 (x2,201), 5 (x127)
	///                 and 10 (x52) on health records.
	///             </item>
	///             <item>
	///                 <c>idx</c> (@0x50) non-zero selects the richer combat-log and floating-number path over the plain one,
	///                 and <c>count</c> (@0x74) tweaks its verb at 5 and 6. Neither touches the stat.
	///             </item>
	///         </list>
	///     </para>
	///     <para>
	///         The client's reader scatters the seven-word tail rather than storing it in order (FUN_00897b60): wire
	///         positions 1..7 land at +0x50, +0x58, +0x54, +0x5c, +0x70, +0x60, +0x64, which is why <c>spellBaseId</c> is the
	///         third tail word on the wire and <c>Idx</c> the first.
	///     </para>
	///     <para>
	///         The class word must not be asserted to be Character. StatDamageIIR_t::ReadSubClass (FUN_008D57C0) opens with
	///         <c>if (this-&gt;carrier-&gt;type != 0xc879) { six reads }</c> and only then reads delta, school and the caster
	///         pair, so the six leading words are conditional on the target having a stat sheet at all. The 13 captured
	///         Destructible records fit exactly: 57 bytes, delta 4676..17606 with school 7 and 10 alternating, caster the
	///         player, and every trailing word zero.
	///     </para>
	///     <para>
	///         Three semantic forms share these words - see <see cref="StatDamages" />:
	///         <list type="bullet">
	///             <item>
	///                 Combat, a spell or damage-over-time tick: statValue is the target's resulting health, delta the signed
	///                 change, spellBaseId the base spell id (wire id shifted right by 8) and idx the effect's slot. Golden:
	///                 frame 6689, Rend ticking the trapper's victim for 8 Slashing.
	///             </item>
	///             <item>
	///                 Environmental, what retail sends when the world damages a player - all 28 fall events in the corpus.
	///                 statValue is the damage magnitude here rather than a resulting health, and delta is exactly its
	///                 negation, proven by consecutive falls: chat-annotated-3 carries 50994, 14076 then 219125 seven seconds
	///                 apart, which cannot be a health track. There is no caster, so casterClass, caster, idx, spellBaseId and
	///                 magnitude are 0 and count is 0 rather than 2, and the school is 12, outside the 0..10 the combat
	///                 schools occupy. No DamageResultIIR accompanies it: a fall renders no floating combat number.
	///             </item>
	///             <item>
	///                 Gauge, the client's draining spellweave bar: statValue is the current bar value, statId the gauge's
	///                 per-type constant, spellBaseId the gauge id and magnitude 80.
	///             </item>
	///         </list>
	///     </para>
	///     <para>
	///         The word after <c>delta</c> is the damage school, not a stat id: every value the corpus carries is 0..10,
	///         exactly the <see cref="DamageSchool" /> range (--report npc-damage).
	///     </para>
	///     <para>The u32 carrier length belongs to the framing sender, not to this body.</para>
	/// </remarks>
	public static class StatDamageCodec
	{
		public const uint Opcode = (uint)IirTypes.StatDamageIIR;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
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

	/// <summary>
	///     The StatDamageIIR body: the seventeen words in wire order, with the still-undecoded ones carried as
	///     <c>Word*</c> fields rather than asserted, so a record with something in them round-trips instead of being
	///     rejected.
	/// </summary>
	/// <remarks>
	///     None of those words is reliably zero. Measured on the health population, DeathReason carries 5 and 10 in 179 of
	///     2,380 records and Word1 carries 12, 5 and 1 in 294 of them.
	/// </remarks>
	public sealed record StatDamageIIR(
		uint Handle,
		byte Env,

		// Not the stat's value after the hit, whatever the name suggests. On the environmental form it is the damage
		// magnitude - the same number Delta carries, negated: 41 of the 1,884 negative-health records in the corpus
		// have StatValue == -Delta, and the environmental population is exactly those 41. On the 1,843 combat ones it
		// is something else. It is not a health pool either: reading a fall's "before" as StatValue + damage makes
		// every fall cost exactly 50% of it, which is arithmetic rather than a measurement, so a fall's health pool
		// has to come from the separate StatUpdateIIR stream.
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

		// The client writes this straight to stat 0x152, CharacterStatId.DeathReason, so it is the cause word rather
		// than filler: what killed you, stated on every hit that might. It also gates the record's only spell lookup
		// (at 10) and, when non-zero, a virtual call on the target. Corpus values on a health record: 0, 5, 10.
		uint DeathReason = 0,
		uint Word11 = 0,
		uint Word14 = 0,
		uint Word15 = 0,
		uint Word16 = 0,

		// The carrier's entity class, and a discriminator rather than a label: EntityClass.Destructible means the six
		// stat-sheet words ahead of Delta are not on the wire at all.
		uint EntityClass = Constants.EntityClass.Character);

	/// <summary>
	///     The three semantic forms of a StatDamageIIR, as value factories. They are factories rather than Build overloads
	///     because the forms collide on signature - a combat tick and an environmental hit are both a handle, three uints
	///     and a school - so choosing between them by parameter order would be ambiguous.
	/// </summary>
	public static class StatDamages
	{
		/// <summary>
		///     The stat id every combat tick carries in the statId slot: HealthCurrent, the stat the tick moves.
		/// </summary>
		public const uint HealthStatId = 0x1B;

		/// <summary>
		///     The spellweave gauges: the stamina gauge that drains while weaving and, once stamina empties, the health
		///     damage-over-time gauge that hurts the caster. Decoded in tools/capture/decode_spellweave.py from
		///     NecroL80_..._consuming-potions-food-spellweaving (caster 0x0802E5D4); see docs/subsystems/combos.md.
		/// </summary>
		public const uint SpellweaveDotGauge = 0x37E690; // "Spellweaving" -> the health DoT when stamina is empty
		public const uint SpellweaveDotScale = 0x0000001B; // v@33 for the DoT gauge
		public const uint SpellweaveStaminaGauge = 0x37E68C; // "Spellweaving Cost" -> the yellow stamina bar
		public const uint SpellweaveStaminaScale = 0x000F425C; // v@33 for the stamina gauge

		/// <summary>The count word both entity-caused forms carry.</summary>
		private const uint CasterCount = 2;

		private const uint SpellweaveGaugeMagnitude = 80;

		/// <summary>A spell or damage-over-time tick from <paramref name="caster" /> on <paramref name="target" />.</summary>
		public static StatDamageIIR Combat(uint target, uint statValue, int delta, DamageSchool school, uint caster, uint idx, uint spellBaseId)
		{
			return new StatDamageIIR(target, 0x00, statValue, HealthStatId, CasterCount, delta, school, EntityClass.Character, caster, idx, spellBaseId, 1);
		}

		/// <summary>
		///     The world damaging <paramref name="target" /> - fall damage and the like. No caster, count 0, and the delta is
		///     the negation of the amount.
		/// </summary>
		public static StatDamageIIR Environmental(uint target, uint amount, uint statId, uint school)
		{
			return new StatDamageIIR(target, 0x00, amount, statId, 0, -(int)amount, (DamageSchool)school, 0, 0, 0, 0, 0);
		}

		/// <summary>
		///     One tick of a draining spellweave gauge. <paramref name="token" /> is the current bar value, draining toward
		///     zero; <paramref name="delta" /> the signed change since the last tick; <paramref name="scale" /> the gauge's
		///     per-type constant. Byte-exact against frame 1654 (stamina) and the damage-over-time frame of the
		///     cancel-weave capture.
		/// </summary>
		public static StatDamageIIR SpellweaveGauge(uint caster, uint token, int delta, uint gaugeId, uint scale)
		{
			return new StatDamageIIR(caster, 0x00, token, scale, CasterCount, delta, (DamageSchool)0, EntityClass.Character, caster, 1, gaugeId, SpellweaveGaugeMagnitude);
		}
	}
}
