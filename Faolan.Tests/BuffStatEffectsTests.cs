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

			Assert.Equal([(CharacterStatId.HitRating, 62)], catalog.BySpellBase[145213]);
			Assert.Equal([(CharacterStatId.Armor, 1000)], catalog.BySpellBase[4239529]);
			Assert.Equal([(CharacterStatId.NaturalManaRegen, 5)], catalog.BySpellBase[145240]);
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

			Assert.Equal([(CharacterStatId.Strength, 2)], catalog.BySpellBase[5070357]);
			Assert.Equal([(CharacterStatId.Dexterity, 2)], catalog.BySpellBase[3200]);
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
			Assert.Equal([(CharacterStatId.CriticalRating, 200)], catalog.PercentBySpellBase[4331123]);
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
	}
}
