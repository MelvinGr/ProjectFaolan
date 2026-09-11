using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Faolan.Importer.Rdb;
using Xunit;

namespace Faolan.Tests
{
	// Kind 53209 `UpgradeCombo`, `[187 combo, 66, 67]` - 3,235 records, the largest single block in the feat catalog.
	//
	// The gate that matters is that both magnitude slots are read. A pair view returns payload words 0 and 1, i.e.
	// (combo, key 66), which makes every cooldown feat read as a no-op with value 0.
	//
	// The names are the client's own: its item-tooltip builder branches on the sign of key 66 to pick between
	// `UpgradeCombo_IncreaseDamage` / `_DecreaseDamage` and on key 67 for the Cooldown pair.
	//
	// Pure by design: synthetic template bodies, no RDB, so they run from a plain clone.
	public class FeatComboModifierTests
	{
		private const uint UpgradeCombo = 53209;

		// `Hexer` - "improves the damage inflicted by the Blood for Aid combos": key 66 = +100 (+1.00%), key 67 = 0.
		[Fact]
		public void UpgradeCombo_ReadsTheDamageAxis()
		{
			var mod = Assert.Single(RdbFeatImporter.ParseComboModifiers(Template(UpgradeCombo, [3137719, 100, 0])));

			Assert.Equal(UpgradeCombo, mod.Kind);
			Assert.Equal(3137719u, mod.ComboId);
			Assert.Equal(100, mod.DamagePercentHundredths);
			Assert.Equal(0, mod.CooldownPercentHundredths);
		}

		// `Dread Lord` - "Reduces the reuse time of all combos": key 67 = -100, and reading it as unsigned reports
		// 4294967196. The pair view does not see this slot at all.
		[Fact]
		public void UpgradeCombo_ReadsTheCooldownAxisAndItIsSigned()
		{
			var mod = Assert.Single(RdbFeatImporter.ParseComboModifiers(Template(UpgradeCombo, [228651, 0, unchecked((uint)-100)])));

			Assert.Equal(228651u, mod.ComboId);
			Assert.Equal(0, mod.DamagePercentHundredths);
			Assert.Equal(-100, mod.CooldownPercentHundredths);
		}

		// Both axes on one record is rare but real (5 of the 3,235 shipped records - the "By imitating the bear" feats
		// carry +20.00% damage and -3.00% cooldown together), so neither slot may be treated as excluding the other.
		[Fact]
		public void UpgradeCombo_CarriesBothAxesWhenTheRecordSetsBoth()
		{
			var mod = Assert.Single(RdbFeatImporter.ParseComboModifiers(Template(UpgradeCombo, [159327, 2000, unchecked((uint)-300)])));

			Assert.Equal(2000, mod.DamagePercentHundredths);
			Assert.Equal(-300, mod.CooldownPercentHundredths);
		}

		// A record that moves neither axis is a combo reference with no magnitude - nothing can consume it, and
		// importing it would put a row in the table that says a feat upgrades a combo by nothing.
		[Fact]
		public void UpgradeCombo_WithNoMagnitude_IsDropped()
		{
			Assert.Empty(RdbFeatImporter.ParseComboModifiers(Template(UpgradeCombo, [228651, 0, 0])));
		}

		// …and a record with no combo is a mis-sliced element, not an upgrade of combo 0.
		[Fact]
		public void UpgradeCombo_WithNoComboReference_IsDropped()
		{
			Assert.Empty(RdbFeatImporter.ParseComboModifiers(Template(UpgradeCombo, [0, 100, 0])));
		}

		// Only kind 53209 lands here. The neighbouring kinds share the record signature, so a parser that keyed off
		// the shape rather than the kind would file a spell-line hit-percent as a combo upgrade.
		[Fact]
		public void OtherKinds_AreNotComboUpgrades()
		{
			var body = Template((53075u, [41000u, 3500u, 1u]), (53045u, [280u, 2000u]), (UpgradeCombo, [228651u, 450u, 0u]));

			var mod = Assert.Single(RdbFeatImporter.ParseComboModifiers(body));
			Assert.Equal(228651u, mod.ComboId);
			Assert.Equal(450, mod.DamagePercentHundredths);
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
