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
	// ONE grammar: the carrier, then exactly SEVENTEEN big-endian words.
	//
	//   [subop][class][handle][u8 env]
	//   [statValue][0][0][0][statId][count][i32 delta][school]
	//   [casterClass][caster][idx][0][spellBaseId][magnitude][0][0][0]
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
		public const uint Opcode = (uint)GameServer0X2000Opcodes.StatDamageIIR;

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
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			var env = r.ReadU8();

			var statValue = r.ReadU32Be();
			var word1 = r.ReadU32Be();
			var word2 = r.ReadU32Be();
			var word3 = r.ReadU32Be();
			var statId = r.ReadU32Be();
			var count = r.ReadU32Be();
			var delta = r.ReadI32Be();
			var school = (DamageSchool)r.ReadU32Be();
			var casterClass = r.ReadU32Be();
			var caster = r.ReadU32Be();
			var idx = r.ReadU32Be();
			var word11 = r.ReadU32Be();
			var spellBaseId = r.ReadU32Be();
			var magnitude = r.ReadU32Be();

			return new StatDamageIIR(handle, env, statValue, statId, count, delta, school, casterClass, caster, idx, spellBaseId, magnitude, word1, word2, word3, word11, r.ReadU32Be(),
				r.ReadU32Be(), r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, StatDamageIIR v)
		{
			s
				.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(v.Handle)
				.WriteByte(v.Env)
				.WriteUInt32(v.StatValue)
				.WriteUInt32(v.Word1)
				.WriteUInt32(v.Word2)
				.WriteUInt32(v.Word3)
				.WriteUInt32(v.StatId)
				.WriteUInt32(v.Count)
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

	// StatDamageIIR (0xE9F24184). The seventeen words in wire order, with the six that are 0 in every captured record
	// carried as `Word*` fields rather than asserted - a record with something in them round-trips instead of being
	// rejected, which is what an undecoded-but-constant word deserves.
	public sealed record StatDamageIIR(
		uint Handle,
		byte Env,
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
		uint Word3 = 0,
		uint Word11 = 0,
		uint Word14 = 0,
		uint Word15 = 0,
		uint Word16 = 0);

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
