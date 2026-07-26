using System.Collections.Generic;
using System.Linq;
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

		// A percent record must never be read as a flat one: Philtre of Guile's 200 is "+2% Critical Rating".
		[Fact]
		public void PercentAndHealthKinds_AreNotApplied()
		{
			var catalog = BuffStatEffects.BuildCatalog([
				Row(4331123, CharacterStatId.CriticalRating, BuffStatEffects.PercentKind, 200), // +2%, not +200
				Row(3666302, CharacterStatId.HealthCurrent, HealthKind, 3400) // Lifestrike: an instant heal
			]);

			Assert.Empty(catalog.BySpellBase);
			Assert.Equal(1, catalog.PercentSkipped);
			Assert.Equal(1, catalog.KindSkipped);
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
			var catalog = BuffStatEffects.BuildCatalog([Row(4239529, CharacterStatId.Armor, FlatKind, 1000)]).BySpellBase;
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
			var catalog = BuffStatEffects.BuildCatalog([Row(4239529, CharacterStatId.Armor, FlatKind, 1000)]).BySpellBase;
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
			var catalog = BuffStatEffects
				.BuildCatalog([Row(4239529, CharacterStatId.Armor, FlatKind, 1000), Row(4121992, CharacterStatId.Armor, FlatKind, 2000)])
				.BySpellBase;
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

			Assert.Empty(BuffStatEffects.Recompute(entity, new Dictionary<uint, (CharacterStatId Stat, int Amount)[]>()));
		}

		// A debuff can take a stat to zero but never below it (the wire value is unsigned).
		[Fact]
		public void DebuffBelowZero_ClampsAtZero()
		{
			var catalog = BuffStatEffects.BuildCatalog([Row(999, CharacterStatId.Armor, FlatKind, -500)]).BySpellBase;
			var entity = Player();
			entity.GearBonuses = new Dictionary<CharacterStatId, int> { [CharacterStatId.Armor] = 100 };

			BuffStatEffects.Recompute(entity, catalog);

			Assert.Equal(0u, entity.ArmorRating);
		}

		// #450: a buff that raises a max pool has to move the POOL, and the client has to be told the whole pool rather
		// than the bonus - otherwise the bar stops at the unbuffed max and out-of-combat regen visibly stalls there.
		[Fact]
		public void MaxPoolBuff_RaisesThePoolAndPushesTheWholePool()
		{
			// Sustaining Wrath (Rank 9): stat 1 = 180000 at the ×100 vitals scale -> +1800 health.
			var catalog = BuffStatEffects.BuildCatalog([Row(4239610, CharacterStatId.HealthMax, 53045, 180000)]).BySpellBase;
			var entity = Player();
			entity.Vitals = EntityVitals.FromCharacter(new Character { Class = Class.BearShaman, Level = 80, MaxStats = new CharacterMaxStats { Health = 6341 } });

			entity.ActiveBuffs[4239610 << 8] = new ActiveBuff(4239610 << 8, entity.Handle, System.DateTimeOffset.UtcNow, 10f);
			var applied = BuffStatEffects.Recompute(entity, catalog);

			Assert.Equal([(CharacterStatId.HealthMax, 8141u)], applied); // 6341 + 1800, not the bare 1800
			Assert.Equal(8141u, entity.Vitals.MaxHealth);
		}

		// …and when it ends the pool comes back down, taking CURRENT with it: a player left above their own bar would
		// never regen and would read as permanently full.
		[Fact]
		public void MaxPoolBuffEnding_LowersThePoolAndClampsCurrent()
		{
			var catalog = BuffStatEffects.BuildCatalog([Row(4239610, CharacterStatId.HealthMax, 53045, 180000)]).BySpellBase;
			var entity = Player();
			entity.Vitals = EntityVitals.FromCharacter(new Character { Class = Class.BearShaman, Level = 80, MaxStats = new CharacterMaxStats { Health = 6341 } });

			entity.ActiveBuffs[4239610 << 8] = new ActiveBuff(4239610 << 8, entity.Handle, System.DateTimeOffset.UtcNow, 10f);
			BuffStatEffects.Recompute(entity, catalog);
			entity.Vitals.Health = 8141;

			entity.ActiveBuffs.Clear();
			var removed = BuffStatEffects.Recompute(entity, catalog);

			Assert.Equal([(CharacterStatId.HealthMax, 6341u)], removed);
			Assert.Equal(6341u, entity.Vitals.MaxHealth);
			Assert.Equal(6341u, entity.Vitals.Health); // clamped down with the max, not left above the bar
		}

		// A max pool is `base + gear + buffs`, and the buff is added to the gear-inclusive base rather than replacing it.
		[Fact]
		public void MaxPoolBuff_StacksOnTopOfGear()
		{
			var catalog = BuffStatEffects.BuildCatalog([Row(4239610, CharacterStatId.HealthMax, 53045, 180000)]).BySpellBase;
			var entity = Player();
			entity.GearBonuses = new Dictionary<CharacterStatId, int> { [CharacterStatId.HealthMax] = 584 };
			entity.Vitals = EntityVitals.FromCharacter(new Character { Class = Class.BearShaman, Level = 80, MaxStats = new CharacterMaxStats { Health = 6341 } },
				entity.GearBonuses);

			Assert.Equal(6925u, entity.Vitals.MaxHealth); // gear alone, before any buff

			entity.ActiveBuffs[4239610 << 8] = new ActiveBuff(4239610 << 8, entity.Handle, System.DateTimeOffset.UtcNow, 10f);
			BuffStatEffects.Recompute(entity, catalog);

			Assert.Equal(8725u, entity.Vitals.MaxHealth); // 6341 + 584 gear + 1800 buff
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
