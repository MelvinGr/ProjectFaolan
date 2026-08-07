using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Importer.Rdb;
using Xunit;

namespace Faolan.Tests
{
	// #593: kind 53250 `ChangeInheritedWeaponAttack`, `[187 combo, 84 attack, 66 dps, 67 crit, 68 hate, 69 type]` -
	// 315 records over 12 feats, and the last kind the ticket's table left undecoded.
	//
	// Every slot is the client's own, from the tooltip branch at `014a1dc5`: it reads the six GameActionStat slots in
	// order, picks `FinishingComboAttack` or `OpeningComboAttack` on key 84, and renders one localised
	// Increase/Decrease pair per non-zero magnitude - `ComboDPS`, `ComboCriticalChance`, `ComboHateAdjustment` - plus
	// `ChangeComboDamageType` for key 69, which it feeds to the damage-type name table rather than printing.
	//
	// Pure by design: synthetic template bodies, no RDB, so they run from a plain clone.
	public class FeatComboAttackModifierTests
	{
		private const uint ChangeInheritedWeaponAttack = 53250;

		// `Fierce Opening` - "Increases the opening damage done by all combos": attack 1, key 66 = 30.
		//
		// ⚠️ 30 is +30%, NOT +0.30%. The same GameActionStat slot on kind 53209 is hundredths of a percent, and the
		// difference is the client's own: 53209's branch multiplies by 0.01 and this one pushes the raw int at the
		// integer formatter with a hardcoded `%`.
		[Fact]
		public void ComboAttack_ReadsTheOpeningDamageAxis()
		{
			var mod = Assert.Single(RdbFeatImporter.ParseComboAttackModifiers(Template(ChangeInheritedWeaponAttack, [3202262, 1, 30, 0, 0, 0])));

			Assert.Equal(ChangeInheritedWeaponAttack, mod.Kind);
			Assert.Equal(3202262u, mod.ComboId);
			Assert.Equal(ComboAttackSlot.Opening, mod.Slot);
			Assert.Equal(30, mod.DpsPercent);
			Assert.Equal(0, mod.CriticalChanceRating);
		}

		// `Crushed Bones` - "Increases the initial damage done by all Internal Bleed combos": attack 0, the FINISHING
		// one, which is what makes the polarity worth pinning. 0 is Finishing and 1 is Opening, not the other way
		// round, and the two feats that say "opening" in their own text are the ones carrying 1.
		[Fact]
		public void ComboAttack_SlotZeroIsTheFinishingAttack()
		{
			var mod = Assert.Single(RdbFeatImporter.ParseComboAttackModifiers(Template(ChangeInheritedWeaponAttack, [3202249, 0, 25, 0, 0, 0])));

			Assert.Equal(ComboAttackSlot.Finishing, mod.Slot);
			Assert.Equal(25, mod.DpsPercent);
		}

		// `Brutal Blows` - "increasing the chance of inflicting a critical injury": key 67 = 400, which the client
		// renders as +10% (× 0.025). Stored RAW, as the record carries it.
		[Fact]
		public void ComboAttack_ReadsTheCriticalChanceAxis()
		{
			var mod = Assert.Single(RdbFeatImporter.ParseComboAttackModifiers(Template(ChangeInheritedWeaponAttack, [3137804, 0, 0, 400, 0, 0])));

			Assert.Equal(400, mod.CriticalChanceRating);
			Assert.Equal(0, mod.DpsPercent);
		}

		// `Improved Shield Mastery`, the only feat in the catalog that moves hate - and a Guardian one, which is
		// exactly where a threat modifier belongs.
		[Fact]
		public void ComboAttack_ReadsTheHateAxis()
		{
			var mod = Assert.Single(RdbFeatImporter.ParseComboAttackModifiers(Template(ChangeInheritedWeaponAttack, [4723862, 0, 0, 0, 5, 0])));

			Assert.Equal(5, mod.HatePercent);
		}

		// Every magnitude is SIGNED - the tooltip picks `Decrease…` over `Increase…` on the sign - so reading one
		// unsigned reports 4294967196 for a -100.
		[Fact]
		public void ComboAttack_MagnitudesAreSigned()
		{
			var mod = Assert.Single(RdbFeatImporter.ParseComboAttackModifiers(
				Template(ChangeInheritedWeaponAttack, [3202262, 1, unchecked((uint)-10), unchecked((uint)-100), unchecked((uint)-2), 0])));

			Assert.Equal(-10, mod.DpsPercent);
			Assert.Equal(-100, mod.CriticalChanceRating);
			Assert.Equal(-2, mod.HatePercent);
		}

		// Key 69 is not a magnitude: the client resolves it through the damage-type name table. Zero on every shipped
		// record, so a non-zero one must land in its own column rather than being read as a number.
		[Fact]
		public void ComboAttack_DamageTypeIsCarriedSeparately()
		{
			var mod = Assert.Single(RdbFeatImporter.ParseComboAttackModifiers(Template(ChangeInheritedWeaponAttack, [3202262, 1, 0, 0, 0, 4])));

			Assert.Equal(4u, mod.DamageTypeId);
			Assert.Equal(0, mod.DpsPercent);
		}

		// A record that moves no axis at all is a combo reference with no magnitude - nothing can consume it.
		[Fact]
		public void ComboAttack_WithNoMagnitude_IsDropped()
		{
			Assert.Empty(RdbFeatImporter.ParseComboAttackModifiers(Template(ChangeInheritedWeaponAttack, [3202262, 1, 0, 0, 0, 0])));
		}

		// …and one with no COMBO is a mis-sliced element, not a modifier of combo 0.
		[Fact]
		public void ComboAttack_WithNoComboReference_IsDropped()
		{
			Assert.Empty(RdbFeatImporter.ParseComboAttackModifiers(Template(ChangeInheritedWeaponAttack, [0, 1, 30, 0, 0, 0])));
		}

		// The client's own branch RETURNS on a selector it does not recognise rather than falling through, so a third
		// value is a mis-slice rather than a third attack we do not model yet - and importing it would put a row in
		// the table under an enum member that does not exist.
		[Fact]
		public void ComboAttack_WithAnUnknownAttackSlot_IsDropped()
		{
			Assert.Empty(RdbFeatImporter.ParseComboAttackModifiers(Template(ChangeInheritedWeaponAttack, [3202262, 2, 30, 0, 0, 0])));
		}

		// Only kind 53250 lands here. 53209 `UpgradeCombo` is the neighbour that matters: it targets the same combo
		// space through the same key 187 and carries key 66/67 in the same two slots, at a DIFFERENT scale - so a
		// parser keying off the shape rather than the kind would file its hundredths as whole percents.
		[Fact]
		public void UpgradeCombo_IsNotAComboAttackModifier()
		{
			var body = Template((53209u, [228651u, 2000u, 0u]), (ChangeInheritedWeaponAttack, [3202262u, 1u, 30u, 0u, 0u, 0u]));

			var mod = Assert.Single(RdbFeatImporter.ParseComboAttackModifiers(body));
			Assert.Equal(3202262u, mod.ComboId);
			Assert.Equal(30, mod.DpsPercent);

			var upgrade = Assert.Single(RdbFeatImporter.ParseComboModifiers(body));
			Assert.Equal(2000, upgrade.DamagePercentHundredths);
		}

		private static byte[] Template(uint kind, uint[] payload)
		{
			return Template((kind, payload));
		}

		private static byte[] Template(params (uint Kind, uint[] Payload)[] elements)
		{
			var words = new List<uint> { 0 };
			foreach (var (kind, payload) in elements)
			{
				words.AddRange([kind, 0u, 4u, 0u, 1u, 0u, 2u, 9u]);
				words.AddRange(payload);
			}

			// A trailing word so the last element is wholly inside the body - a truncated one is skipped by design.
			words.Add(0);

			var body = new byte[12 + (words.Count * 4)];
			for (var i = 0; i < words.Count; i++)
				BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12 + (i * 4)), words[i]);

			return body;
		}
	}
}
