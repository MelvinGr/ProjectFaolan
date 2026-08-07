using System.Collections.Generic;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// The buff stat engine (#367): which imported [2][9] records it honors, at what scale, and that a buff's stats go
	// back DOWN when it ends. The DATA (kinds, stats, amounts) is the decoded spell region
	// (docs/rdb/spell-stat-effects.md); this pins the policy and the arithmetic.
	public class BuffStatEffectsTests
	{
		private const uint FlatKind = 53220; // Battle Cry / Fend / Shielding Litany
		private const uint AttributeKind = 53189; // the potions
		private const uint HealthKind = 53219; // Lifestrike's heal - not a buff stat
		private const uint TempChangeSkill = 53110; // Skin of Iron / Soul of Mitra - `Modify` with a duration in its name

		// A rating's amount is stored at the same wire scale item modifiers use, because it IS the same field: +62 Hit
		// Rating on a buff and on a ring are the same number in the same units.
		[Fact]
		public void FlatRating_KeepsItsWireScale()
		{
			var catalog = BuffStatEffects.BuildCatalog([
				Row(145213, CharacterStatId.HitRating, FlatKind, 62), // Battle Cry: Marshal the Ranks (Rank 3)
				Row(4239529, CharacterStatId.Armor, FlatKind, 1000), // Fend
				Row(145240, CharacterStatId.NaturalManaRegen, FlatKind, 500) // a regen formation: ×100 -> +5/s
			]);

			Assert.Equal([Delta(CharacterStatId.HitRating, 62)], catalog.BySpellBase[145213]);
			Assert.Equal([Delta(CharacterStatId.Armor, 1000)], catalog.BySpellBase[4239529]);
			Assert.Equal([Delta(CharacterStatId.NaturalManaRegen, 5)], catalog.BySpellBase[145240]);
		}

		// Attributes are the one place the spell region and the item region disagree: a spell stores DISPLAY units.
		// Reading "Potion of Strength" 0x324 = 2 at the item scale (×10) would grant +0.2 Strength.
		[Fact]
		public void FlatAttribute_IsDisplayUnitsNotTheItemScale()
		{
			var catalog = BuffStatEffects.BuildCatalog([
				Row(5070357, CharacterStatId.Strength, AttributeKind, 2), // Potion of Strength (4h)
				Row(3200, CharacterStatId.Dexterity, 53045, 2) // "Dexterity Buff 2" - the client's own text says "de 2"
			]);

			Assert.Equal([Delta(CharacterStatId.Strength, 2)], catalog.BySpellBase[5070357]);
			Assert.Equal([Delta(CharacterStatId.Dexterity, 2)], catalog.BySpellBase[3200]);
			Assert.Equal(1u, BuffStatEffects.SpellScale(CharacterStatId.Strength));
			Assert.Equal(10u, CharacterStatId.Strength.GetScale()); // …while an ITEM's Strength really is ×10
		}

		// A percent record must never be read as a flat one: Philtre of Guile's 200 is "+2% Critical Rating", not +200.
		// It is kept apart from the flat deltas, and a health-effect kind is not a buff stat at all.
		[Fact]
		public void PercentRecords_AreKeptApartFromFlatOnes()
		{
			var catalog = BuffStatEffects.BuildCatalog([
				Row(4331123, CharacterStatId.CriticalRating, BuffStatEffects.PercentKind, 200), // +2%, not +200
				Row(3666302, CharacterStatId.HealthCurrent, HealthKind, 3400) // Lifestrike: an instant heal
			]);

			Assert.Empty(catalog.BySpellBase);
			Assert.Equal([Delta(CharacterStatId.CriticalRating, 200)], catalog.PercentBySpellBase[4331123]);
			Assert.Equal(1, catalog.KindSkipped);
		}

		// A percent on a stat whose scale we have NOT verified is counted, not applied - the same gate the flat kinds
		// pass through, because a percentage of an unknown quantity is no more usable than a flat amount of it.
		[Fact]
		public void PercentOnAnUnmodelledStat_IsCountedNotApplied()
		{
			// "Absolute Pharmacon of Nullification": Evade Rating (308) and Immunity Rating (490), neither modelled.
			var catalog = BuffStatEffects.BuildCatalog([
				Row(5003553, (CharacterStatId)308, BuffStatEffects.PercentKind, 750),
				Row(5003553, (CharacterStatId)490, BuffStatEffects.PercentKind, 750)
			]);

			Assert.Empty(catalog.PercentBySpellBase);
			Assert.Equal(2, catalog.PercentSkipped);
		}

		// #436: a percentage SCALES the flat total rather than adding to it - "+2% Critical Rating" is worth more on a
		// well-geared character, which is the whole reason it needed a model of its own.
		[Fact]
		public void PercentBuff_ScalesTheFlatTotal()
		{
			var catalog = BuffStatEffects.BuildCatalog([Row(4331123, CharacterStatId.CriticalRating, BuffStatEffects.PercentKind, 200)]);
			var entity = Player();
			entity.GearBonuses = new Dictionary<CharacterStatId, int> { [CharacterStatId.CriticalRating] = 250 };

			entity.ActiveBuffs[4331123 << 8] = new ActiveBuff(4331123 << 8, entity.Handle, System.DateTimeOffset.UtcNow, 10f);
			var applied = BuffStatEffects.Recompute(entity, catalog);

			Assert.Equal([(CharacterStatId.CriticalRating, 255u)], applied); // 250 x 1.02
			Assert.Equal(255u, entity.CriticalRating);

			entity.ActiveBuffs.Clear();
			BuffStatEffects.Recompute(entity, catalog);
			Assert.Equal(250u, entity.CriticalRating); // and it comes back off
		}

		// A percentage applies AFTER the flat deltas, on their total - so a flat +50 and a +2% are 306, not 305.
		[Fact]
		public void PercentBuff_AppliesToTheFlatTotalNotToGearAlone()
		{
			var catalog = BuffStatEffects.BuildCatalog([
				Row(4331123, CharacterStatId.CriticalRating, BuffStatEffects.PercentKind, 200),
				Row(145213, CharacterStatId.CriticalRating, FlatKind, 50)
			]);
			var entity = Player();
			entity.GearBonuses = new Dictionary<CharacterStatId, int> { [CharacterStatId.CriticalRating] = 250 };

			entity.ActiveBuffs[4331123 << 8] = new ActiveBuff(4331123 << 8, entity.Handle, System.DateTimeOffset.UtcNow, 10f);
			entity.ActiveBuffs[145213 << 8] = new ActiveBuff(145213 << 8, entity.Handle, System.DateTimeOffset.UtcNow, 10f);
			BuffStatEffects.Recompute(entity, catalog);

			Assert.Equal(306u, entity.CriticalRating); // (250 + 50) x 1.02
		}

		// Two percentages ADD rather than compounding: +2% and +5% is +7%, not 1.02 x 1.05.
		[Fact]
		public void TwoPercentBuffs_AddRatherThanCompound()
		{
			var catalog = BuffStatEffects.BuildCatalog([
				Row(4331123, CharacterStatId.CriticalRating, BuffStatEffects.PercentKind, 200), // Philtre of Guile
				Row(4331122, CharacterStatId.CriticalRating, BuffStatEffects.PercentKind, 500) // Refined Philtre of Guile
			]);
			var entity = Player();
			entity.GearBonuses = new Dictionary<CharacterStatId, int> { [CharacterStatId.CriticalRating] = 1000 };

			entity.ActiveBuffs[4331123 << 8] = new ActiveBuff(4331123 << 8, entity.Handle, System.DateTimeOffset.UtcNow, 10f);
			entity.ActiveBuffs[4331122 << 8] = new ActiveBuff(4331122 << 8, entity.Handle, System.DateTimeOffset.UtcNow, 10f);
			BuffStatEffects.Recompute(entity, catalog);

			Assert.Equal(1070u, entity.CriticalRating); // 1000 x 1.07, not 1071
		}

		// #599: the interpolated kinds are a LEVEL RAMP, so the same buff is worth different amounts to a level-20 and
		// a level-80 carrier. The gate is on the RECOMPUTE rather than the catalog, because the catalog cannot resolve
		// one - it has no carrier - which is the whole reason the ramp is carried into it rather than collapsed there.
		[Theory]
		[InlineData(5u, 1000u)]  // below the ramp: clamped to the low endpoint
		[InlineData(10u, 1000u)] // at the low level
		[InlineData(30u, 3000u)] // half way
		[InlineData(50u, 5000u)] // at the high level
		[InlineData(80u, 5000u)] // above it: clamped to the high endpoint
		public void RampedBuff_IsWorthTheValueAtTheCarriersLevel(uint level, uint expected)
		{
			// A ramp shaped like the shipped ones: 1000 armor at level 10 rising to 5000 at 50. Armor's wire scale is
			// 1, so the display value IS the raw one and this test is purely about the interpolation.
			var catalog = BuffStatEffects.BuildCatalog([RampRow(4239529, CharacterStatId.Armor, FlatKind, 1000, 5000, 10, 50)]);
			var entity = Player();
			entity.Level = level;

			entity.ActiveBuffs[4239529 << 8] = new ActiveBuff(4239529 << 8, entity.Handle, System.DateTimeOffset.UtcNow, 10f);
			BuffStatEffects.Recompute(entity, catalog);

			Assert.Equal(expected, entity.ArmorRating);
		}

		// The ramp interpolates in the RAW units it was decoded in and divides by the stat's scale AFTERWARDS. Dividing
		// the endpoints first truncates twice, and the two readings really do part company: a 550 -> 950 regen ramp
		// (×100, so +5.5/s to +9.5/s) is +9/s nine tenths of the way up, while pre-divided endpoints of 5 and 9 give
		// 5 + 0.9 x 4 = 8.6 -> +8/s. This is the reason a ramped row is the one shape BuildCatalog cannot pre-convert.
		[Fact]
		public void RampedBuff_ScalesAfterInterpolatingNotBefore()
		{
			var catalog = BuffStatEffects.BuildCatalog([RampRow(145240, CharacterStatId.NaturalManaRegen, FlatKind, 550, 950, 20, 60)]);
			var entity = Player();
			entity.Level = 56; // nine tenths of the way from 20 to 60

			entity.ActiveBuffs[145240 << 8] = new ActiveBuff(145240 << 8, entity.Handle, System.DateTimeOffset.UtcNow, 10f);
			BuffStatEffects.Recompute(entity, catalog);

			Assert.Equal(9u, entity.NaturalManaRegen); // (550 + 0.9 x 400) / 100 = 909 / 100 = 9, not 8
		}

		// A flat record on a stat whose scale we have not verified is imported but inert - counted, not guessed at.
		[Fact]
		public void FlatRecordOnAnUnmodelledStat_IsCountedNotApplied()
		{
			var catalog = BuffStatEffects.BuildCatalog([
				Row(222833, (CharacterStatId)975, 53221, 250800), // Runed Flesh: "Absorb", scale unverified
				Row(216453, (CharacterStatId)977, FlatKind, 1466) // Shield of Infernal Flames: Retributive Damage
			]);

			Assert.Empty(catalog.BySpellBase);
			Assert.Equal(2, catalog.StatSkipped);
		}

		// The three RESISTANCE stats used to fall through that same gate, for no reason: all three are x1, so there
		// was no scale to verify. Between them they were dropping 370 imported rows - including the `-20 Chance of
		// Being Staggered` every horse's `Mounted` buff carries, which is visible in the client's own buff tooltip
		// while the number went nowhere.
		[Fact]
		public void TheResistanceStats_AreHonoredRatherThanSkipped()
		{
			var catalog = BuffStatEffects.BuildCatalog([
				Row(3271454, CharacterStatId.StaggeringChance, FlatKind, -20), // Mounted
				Row(4292427, CharacterStatId.KnockbackResistance, FlatKind, 100), // Turtle Formation
				Row(3155969, CharacterStatId.DamageDeflection, FlatKind, 40) // Phoenix Cloak (Rank 2)
			]);

			Assert.Equal(0, catalog.StatSkipped);
			Assert.Equal(3, catalog.BySpellBase.Count);
			Assert.Equal(-20, catalog.BySpellBase[3271454][0].Amount);
		}

		// KnockbackResistance is the one of the three with a LIVE consumer - `KnockbackService` reads it off
		// `BuffedStats` before falling back to the gear bonus - so honoring it is what makes a resisting BUFF resist a
		// knockback rather than only a resisting item.
		[Fact]
		public void KnockbackResistanceFromABuff_ReachesTheLiveStats()
		{
			var catalog = BuffStatEffects.BuildCatalog([Row(4292427, CharacterStatId.KnockbackResistance, FlatKind, 100)]);
			var entity = Player();

			entity.ActiveBuffs[4292427 << 8] = new ActiveBuff(4292427 << 8, entity.Handle, System.DateTimeOffset.UtcNow, 10f);
			BuffStatEffects.Recompute(entity, catalog);

			Assert.Equal(100u, entity.BuffedStats[CharacterStatId.KnockbackResistance]);
		}

		// ⚠️ The admission test for `HonoredStats`. A stat belongs there when its records ADD to a ZERO baseline,
		// whatever their scale - which is why the whole invulnerability family and the out-of-combat regens went in,
		// and why the MULTIPLIER stats did not: `Recompute` computes `gear + sum(buffs)`, and `Turtle Formation`'s
		// -50 `MovementMultiplier` summed from zero says "movement 50%" for a buff that means "-50%".
		//
		// The gate's ORIGINAL test - "is the wire scale verified" - no longer excludes anything, because
		// `StatDisplayScale` transcribes the client's own divisor rule and covers every id. Reading it as the live
		// test is how entries rot on the wrong side of the list.
		[Fact]
		public void AdditiveFamilies_AreHonoredWhileMultiplierStatsStayOut()
		{
			var catalog = BuffStatEffects.BuildCatalog([
				Row(4239530, CharacterStatId.ColdInvulnerability, FlatKind, 10000), // Magic Shield: +100% cold
				Row(4239531, CharacterStatId.NonCombatHealthRegen, FlatKind, 2640), // Hearty Meal, top rank
				Row(4239532, CharacterStatId.EvadeChance, FlatKind, 2500), // Heightened Sense: +25%
				Row(4292427, CharacterStatId.MovementMultiplier, FlatKind, -50) // Turtle Formation - baseline is 100
			]);

			Assert.Equal(3, catalog.BySpellBase.Count);
			Assert.Equal(1, catalog.StatSkipped);
			Assert.DoesNotContain(4292427u, catalog.BySpellBase.Keys);
		}

		// ⚠️ The admission test for the KIND half of the gate, which is a different question and used to be answered
		// less carefully. 53110 `TempChangeSkill` sat outside `FlatKinds` as "unclassified" while its own RAMPED twin
		// 53221 `InterpolatedTempChangeSkill` sat inside it - the list disagreeing with itself, since 53221 is to
		// 53110 exactly what the already-honored 53220 is to 53045.
		//
		// What settles it is the UNIT, not the name: on stats whose scale is already proven, `TempChangeSkill`'s
		// amounts land in that same unit rather than a rescaled one. `Skin of Iron` carries the same +10000 = +100%
		// step on four schools that `Magic Shield` carries with 53045 on cold, and `Soul of Mitra` - whose client
		// description is "shielding the priest from almost all damage" - carries 90 deflection beside `Phoenix
		// Cloak`'s 53045 +40. Both were landing nowhere.
		[Fact]
		public void TempChangeSkill_IsFlat_LikeItsAlreadyHonoredInterpolatedTwin()
		{
			var catalog = BuffStatEffects.BuildCatalog([
				Row(166828, CharacterStatId.SlashingInvulnerability, TempChangeSkill, 10000), // Skin of Iron
				Row(197061, CharacterStatId.DamageDeflection, TempChangeSkill, 90) // Soul of Mitra
			]);

			Assert.Equal(0, catalog.KindSkipped);

			// In DISPLAY units, like every other flat delta: the invulnerabilities are ×100, so the 10000 the record
			// carries is the +100% the client draws - the same number `Magic Shield` produces through 53045. Damage
			// deflection is ×1, so 90 stays 90.
			Assert.Equal([Delta(CharacterStatId.SlashingInvulnerability, 100)], catalog.BySpellBase[166828]);
			Assert.Equal([Delta(CharacterStatId.DamageDeflection, 90)], catalog.BySpellBase[197061]);
		}

		// ...and admitting the kind does NOT admit what else it carries. 53110's bulk is a POLYMORPH tail - a
		// shapeshift buff assigning a head mesh or a creature scale - which is not `gear + sum(buffs)` in any units,
		// and which the STAT half of the gate goes on holding. The two halves are independent, which is the reason
		// widening one was safe.
		[Fact]
		public void TempChangeSkill_DoesNotSmuggleInThePolymorphTail()
		{
			var catalog = BuffStatEffects.BuildCatalog([
				Row(4934382, (CharacterStatId)64, TempChangeSkill, 4048194), // Head Mesh: an RDB record id
				Row(4934382, CharacterStatId.Scale, TempChangeSkill, 300)
			]);

			Assert.Empty(catalog.BySpellBase);
			Assert.Equal(2, catalog.StatSkipped);
		}

		// The gate answers for a row from ONE place, because the imported-vs-applied census is taken from outside the
		// server (`--report spell-effect-gap`) and a census that re-derives the rule drifts from it. #701 is the
		// cautionary case: it was filed off a hand-count that had gone four times stale and named the wrong gate as
		// the problem.
		[Fact]
		public void ClassifyAndBuildCatalog_AreTheSamePolicy()
		{
			SpellStatEffect[] rows =
			[
				Row(145213, CharacterStatId.HitRating, FlatKind, 62), // honored, flat
				Row(4331123, CharacterStatId.CriticalRating, BuffStatEffects.PercentKind, 200), // honored, percent
				Row(4239189, CharacterStatId.HealthMax, 53026, 100000), // `Set` - an assignment, not a delta
				Row(216453, (CharacterStatId)977, FlatKind, 1466), // Retributive Damage - stat not modelled
				Row(5003553, (CharacterStatId)490, BuffStatEffects.PercentKind, 750) // percent on an unmodelled stat
			];

			Assert.Equal([
				BuffStatEffects.Gate.Flat, BuffStatEffects.Gate.Percent, BuffStatEffects.Gate.UnhandledKind,
				BuffStatEffects.Gate.UnmodelledStat, BuffStatEffects.Gate.PercentOnUnmodelledStat
			], System.Array.ConvertAll(rows, BuffStatEffects.Classify));

			var catalog = BuffStatEffects.BuildCatalog(rows);
			Assert.Equal(1, catalog.KindSkipped);
			Assert.Equal(1, catalog.StatSkipped);
			Assert.Equal(1, catalog.PercentSkipped);
		}

		// The whole point: a buff raises the live stat, and losing the buff puts it back to the gear baseline.
		[Fact]
		public void BuffRaisesTheLiveStat_AndEndingItRestoresTheGearBaseline()
		{
			var catalog = BuffStatEffects.BuildCatalog([Row(4239529, CharacterStatId.Armor, FlatKind, 1000)]);
			var entity = Player();
			entity.GearBonuses = new Dictionary<CharacterStatId, int> { [CharacterStatId.Armor] = 250 };
			entity.ArmorRating = 250;

			entity.ActiveBuffs[4239529 << 8] = new ActiveBuff(4239529 << 8, entity.Handle, System.DateTimeOffset.UtcNow, 10f);
			var applied = BuffStatEffects.Recompute(entity, catalog);

			Assert.Equal([(CharacterStatId.Armor, 1250u)], applied);
			Assert.Equal(1250u, entity.ArmorRating);

			entity.ActiveBuffs.Clear();
			var removed = BuffStatEffects.Recompute(entity, catalog);

			Assert.Equal([(CharacterStatId.Armor, 250u)], removed);
			Assert.Equal(250u, entity.ArmorRating); // back to gear alone, not 0 and not still buffed
		}

		// Applying the same buff twice (a refresh, or a relog re-apply) must not stack it - the sum is recomputed from
		// the live buff set, so a re-apply is idempotent.
		[Fact]
		public void ReapplyingTheSameBuff_DoesNotStack()
		{
			var catalog = BuffStatEffects.BuildCatalog([Row(4239529, CharacterStatId.Armor, FlatKind, 1000)]);
			var entity = Player();

			entity.ActiveBuffs[4239529 << 8] = new ActiveBuff(4239529 << 8, entity.Handle, System.DateTimeOffset.UtcNow, 10f);
			BuffStatEffects.Recompute(entity, catalog);
			entity.ActiveBuffs[4239529 << 8] = new ActiveBuff(4239529 << 8, entity.Handle, System.DateTimeOffset.UtcNow, 10f);
			var second = BuffStatEffects.Recompute(entity, catalog);

			Assert.Empty(second); // nothing moved, so nothing is pushed
			Assert.Equal(1000u, entity.ArmorRating);
		}

		// Two buffs granting the same stat DO stack with each other.
		[Fact]
		public void SeparateBuffsOnTheSameStat_Sum()
		{
			var catalog = BuffStatEffects.BuildCatalog([
				Row(4239529, CharacterStatId.Armor, FlatKind, 1000), Row(4121992, CharacterStatId.Armor, FlatKind, 2000)
			]);
			var entity = Player();

			entity.ActiveBuffs[4239529 << 8] = new ActiveBuff(4239529 << 8, entity.Handle, System.DateTimeOffset.UtcNow, 10f);
			entity.ActiveBuffs[4121992 << 8] = new ActiveBuff(4121992 << 8, entity.Handle, System.DateTimeOffset.UtcNow, 10f);

			BuffStatEffects.Recompute(entity, catalog);

			Assert.Equal(3000u, entity.ArmorRating);
		}

		// A player carrying only buffs with no honored deltas (the common case - Shield of the Risen, the mount buff,
		// a DoT debuff) produces no stat push at all.
		[Fact]
		public void BuffsWithoutHonoredDeltas_PushNothing()
		{
			var entity = Player();
			entity.ActiveBuffs[4061768 << 8] = new ActiveBuff(4061768 << 8, entity.Handle, System.DateTimeOffset.UtcNow, 3600f); // Shield of the Risen

			Assert.Empty(BuffStatEffects.Recompute(entity, BuffStatEffects.BuildCatalog([])));
		}

		// A debuff can take a stat to zero but never below it (the wire value is unsigned).
		[Fact]
		public void DebuffBelowZero_ClampsAtZero()
		{
			var catalog = BuffStatEffects.BuildCatalog([Row(999, CharacterStatId.Armor, FlatKind, -500)]);
			var entity = Player();
			entity.GearBonuses = new Dictionary<CharacterStatId, int> { [CharacterStatId.Armor] = 100 };

			// ⚠️ The debuff has to be ACTIVE. Without this line the catalog row never applies and the assertion below
			// passed on an ArmorRating that had simply never been written - which is what it did until #473 made the
			// recompute apply gear-only values too. 100 + (-500) = -400, clamped to 0.
			entity.ActiveBuffs[999 << 8] = new ActiveBuff(999 << 8, entity.Handle, System.DateTimeOffset.UtcNow, 60f);

			BuffStatEffects.Recompute(entity, catalog);

			Assert.Equal(0u, entity.ArmorRating);
		}

		// #450: a buff that raises a max pool has to move the POOL, and the client has to be told the whole pool rather
		// than the bonus - otherwise the bar stops at the unbuffed max and out-of-combat regen visibly stalls there.
		[Fact]
		public void MaxPoolBuff_RaisesThePoolAndPushesTheWholePool()
		{
			// Sustaining Wrath (Rank 9): stat 1 = 180000 at the ×100 vitals scale -> +1800 health.
			var catalog = BuffStatEffects.BuildCatalog([Row(4239610, CharacterStatId.HealthMax, 53045, 180000)]);
			var entity = Player();
			// The base is DERIVED from (Class, Level) (#578), so take it from the character rather than restating a
			// number - what this test is about is the +1800 landing ON it, not what it happens to be.
			var character = new Character { Class = ConanClass.BearShaman, Level = 80 };
			entity.Vitals = EntityVitals.FromCharacter(character);
			var baseHp = entity.Vitals.BaseMaxHealth;

			entity.ActiveBuffs[4239610 << 8] = new ActiveBuff(4239610 << 8, entity.Handle, System.DateTimeOffset.UtcNow, 10f);
			var applied = BuffStatEffects.Recompute(entity, catalog);

			Assert.Equal([(CharacterStatId.HealthMax, baseHp + 1800)], applied); // the whole pool, not the bare 1800
			Assert.Equal(baseHp + 1800, entity.Vitals.MaxHealth);
		}

		// …and when it ends the pool comes back down, taking CURRENT with it: a player left above their own bar would
		// never regen and would read as permanently full.
		[Fact]
		public void MaxPoolBuffEnding_LowersThePoolAndClampsCurrent()
		{
			var catalog = BuffStatEffects.BuildCatalog([Row(4239610, CharacterStatId.HealthMax, 53045, 180000)]);
			var entity = Player();
			var character = new Character { Class = ConanClass.BearShaman, Level = 80 };
			entity.Vitals = EntityVitals.FromCharacter(character);
			var baseHp = entity.Vitals.BaseMaxHealth;

			entity.ActiveBuffs[4239610 << 8] = new ActiveBuff(4239610 << 8, entity.Handle, System.DateTimeOffset.UtcNow, 10f);
			BuffStatEffects.Recompute(entity, catalog);
			entity.Vitals.Health = baseHp + 1800;

			entity.ActiveBuffs.Clear();
			var removed = BuffStatEffects.Recompute(entity, catalog);

			Assert.Equal([(CharacterStatId.HealthMax, baseHp)], removed);
			Assert.Equal(baseHp, entity.Vitals.MaxHealth);
			Assert.Equal(baseHp, entity.Vitals.Health); // clamped down with the max, not left above the bar
		}

		// A max pool is `base + gear + buffs`, and the buff is added to the gear-inclusive base rather than replacing it.
		[Fact]
		public void MaxPoolBuff_StacksOnTopOfGear()
		{
			var catalog = BuffStatEffects.BuildCatalog([Row(4239610, CharacterStatId.HealthMax, 53045, 180000)]);
			var entity = Player();
			entity.GearBonuses = new Dictionary<CharacterStatId, int> { [CharacterStatId.HealthMax] = 584 };
			var character = new Character { Class = ConanClass.BearShaman, Level = 80 };
			entity.Vitals = EntityVitals.FromCharacter(character, entity.GearBonuses);
			// The pool BEFORE gear and buffs - taken off the same object the test then buffs, so the two cannot
			// disagree about how it was derived.
			var baseHp = entity.Vitals.BaseMaxHealth;

			Assert.Equal(baseHp + 584, entity.Vitals.MaxHealth); // gear alone, before any buff

			entity.ActiveBuffs[4239610 << 8] = new ActiveBuff(4239610 << 8, entity.Handle, System.DateTimeOffset.UtcNow, 10f);
			BuffStatEffects.Recompute(entity, catalog);

			Assert.Equal(baseHp + 584 + 1800, entity.Vitals.MaxHealth); // base + 584 gear + 1800 buff
		}

		// #473: a GEAR-ONLY change to a max pool must APPLY, with no buff involved. The recompute used to `continue`
		// before calling SetMaxFromBonus whenever a stat was unseeded with no flat/percent delta - safe while
		// GearBonuses was fixed at world entry, and wrong the moment equipping refreshes it mid-session. The entry was
		// never seeded either, so the skip was permanent: a +25 HP item moved the server's max by nothing at all.
		[Fact]
		public void GearOnlyChangeToAMaxPool_IsApplied()
		{
			var catalog = BuffStatEffects.BuildCatalog([]); // no buffs anywhere - gear is the only source
			var entity = Player();
			// FromCharacter, not FromMax: only it seeds BaseMaxHealth, which is what SetMaxFromBonus adds the bonus
			// ONTO. (Production always comes through FromCharacter - GetOrAddPlayerVitals and RegisterPlayerInWorld.)
			var character = new Character { Class = ConanClass.Guardian, Level = 1 };
			entity.Vitals = EntityVitals.FromCharacter(character);
			var baseHp = entity.Vitals.BaseMaxHealth;
			entity.GearBonuses = new Dictionary<CharacterStatId, int> { [CharacterStatId.HealthMax] = 0 };

			// First sync seeds off gear alone: it APPLIES but reports nothing (that is not news).
			Assert.Empty(BuffStatEffects.Recompute(entity, catalog));
			Assert.Equal(baseHp, entity.Vitals.MaxHealth);

			// Now equip a +25 HP piece. The max must move, and be reported so the caller can act on it.
			entity.GearBonuses = new Dictionary<CharacterStatId, int> { [CharacterStatId.HealthMax] = 25 };
			var changed = BuffStatEffects.Recompute(entity, catalog);

			Assert.Equal(baseHp + 25, entity.Vitals.MaxHealth);
			Assert.Contains(changed, c => c.Stat == CharacterStatId.HealthMax);

			// And taking it off comes back down, clamping current with it.
			entity.GearBonuses = new Dictionary<CharacterStatId, int> { [CharacterStatId.HealthMax] = 0 };
			BuffStatEffects.Recompute(entity, catalog);
			Assert.Equal(baseHp, entity.Vitals.MaxHealth);
			Assert.Equal(baseHp, entity.Vitals.Health); // current clamped down, not left above the bar
		}

		// #584: a buff that raises an ATTRIBUTE has to move the pool that attribute scales. Each pool is
		// `class base + Σ weight × attribute + class row + flat bonuses`, and only the flat half was tracked live, so
		// a +10 Constitution buff on an Assassin (health weight 8) was worth 0 instead of +80.
		[Fact]
		public void AttributeBuff_MovesThePoolItScales()
		{
			// Constitution is stored in DISPLAY units on a spell record (see BuffStatEffects.SpellScale), so 10 = +10.
			var catalog = BuffStatEffects.BuildCatalog([Row(3197, CharacterStatId.Constitution, FlatKind, 10)]);
			var entity = Player();
			entity.CharacterClass = ConanClass.Assassin;
			entity.Level = 1;
			entity.Vitals = EntityVitals.FromCharacter(new Character { Class = ConanClass.Assassin, Level = 1 }, null,
				ClassAttributeBase());
			entity.ClassBase = ClassAttributeBase();

			// Seed the unbuffed state the way world entry does, so the assertion is about the CHANGE.
			BuffStatEffects.Recompute(entity, catalog);
			var before = entity.Vitals.MaxHealth;
			Assert.Equal(1u, entity.Attributes.Constitution); // the level-1 class row: a flat 10 wire = 1 display

			entity.ActiveBuffs[3197 << 8] = new ActiveBuff(3197 << 8, entity.Handle, System.DateTimeOffset.UtcNow, 10f);
			var applied = BuffStatEffects.Recompute(entity, catalog);

			Assert.Equal(11u, entity.Attributes.Constitution);
			// The Assassin's health weight is 8000 thousandths, i.e. 8 display health per point of Constitution.
			Assert.Equal(before + 80, entity.Vitals.MaxHealth);
			// ...and the client is TOLD, or its bar stops at the old ceiling.
			Assert.Contains(applied, a => a.Stat == CharacterStatId.HealthMax && a.Value == entity.Vitals.MaxHealth);

			// And it comes back off when the buff ends.
			entity.ActiveBuffs.Clear();
			BuffStatEffects.Recompute(entity, catalog);
			Assert.Equal(before, entity.Vitals.MaxHealth);
		}

		// The attribute term must not EAT the flat one: a pool carrying both a +Constitution buff and a flat +HealthMax
		// bonus has to end up with the sum, which is what SetMaxFromBonus-on-top-of-the-rebase means.
		[Fact]
		public void AttributeBuff_AddsToTheFlatPoolBonusRatherThanReplacingIt()
		{
			var catalog = BuffStatEffects.BuildCatalog([Row(3197, CharacterStatId.Constitution, FlatKind, 10)]);
			var entity = Player();
			entity.CharacterClass = ConanClass.Assassin;
			entity.Level = 1;
			entity.ClassBase = ClassAttributeBase();
			entity.GearBonuses = new Dictionary<CharacterStatId, int> { [CharacterStatId.HealthMax] = 25 };
			entity.Vitals = EntityVitals.FromCharacter(new Character { Class = ConanClass.Assassin, Level = 1 },
				entity.GearBonuses, entity.ClassBase);

			BuffStatEffects.Recompute(entity, catalog);
			var before = entity.Vitals.MaxHealth;

			entity.ActiveBuffs[3197 << 8] = new ActiveBuff(3197 << 8, entity.Handle, System.DateTimeOffset.UtcNow, 10f);
			BuffStatEffects.Recompute(entity, catalog);

			Assert.Equal(before + 80, entity.Vitals.MaxHealth); // the +25 ring is still in there
		}

		// The level-1 ClassAttributeBases row: a flat 10 wire (= 1 display) for all five attributes of all twelve
		// classes, which is what VitalAttributes.LevelOneBase documents.
		private static Dictionary<CharacterStatId, int> ClassAttributeBase()
		{
			return new Dictionary<CharacterStatId, int>
			{
				[CharacterStatId.Strength] = 10,
				[CharacterStatId.Constitution] = 10,
				[CharacterStatId.Intelligence] = 10,
				[CharacterStatId.Wisdom] = 10,
				[CharacterStatId.Dexterity] = 10
			};
		}

		private static WorldEntity Player()
		{
			return new WorldEntity(0x1000, 2040, true, "Tester", null!);
		}

		private static SpellStatEffect Row(uint spellId, CharacterStatId stat, uint kind, int amount)
		{
			return new SpellStatEffect
			{
				SpellId = spellId,
				StatId = stat,
				EffectKind = kind,
				Amount = amount
			};
		}

		// A ramped row (#599): `Amount` is the ramp's LOW endpoint and the other three columns carry the rest.
		private static SpellStatEffect RampRow(uint spellId, CharacterStatId stat, uint kind, int low, int high, uint lowLevel, uint highLevel)
		{
			var row = Row(spellId, stat, kind, low);
			row.HighAmount = high;
			row.LowLevel = lowLevel;
			row.HighLevel = highLevel;
			return row;
		}

		// The catalog entry a flat row builds - spelled out so the assertions above stay about the AMOUNT rather than
		// about the shape the catalog happens to hold it in.
		private static BuffStatEffects.Delta Delta(CharacterStatId stat, int amount)
		{
			return new BuffStatEffects.Delta(stat, amount);
		}
	}
}
