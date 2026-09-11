using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Faolan.Core.Rdb;
using Faolan.GameServer.Combat;
using Xunit;

namespace Faolan.Tests
{
	// A feat's spell modifier - "+35% damage or healing on Flesh to Worms". It sits in the same template region
	// and carries the same signature as a stat modifier, and the only thing that tells them apart is whether the
	// payload's first word is a stat id or a spell-line id. That is why these were invisible for so long: the shared
	// parser found them and dropped them on its stat-id bound.
	public class FeatSpellModifierTests
	{
		// Decomposition rank 5 (template 1173628), the case decoded against the in-client tooltip: kind 53075,
		// payload (41000, 3500) = Necromancer line 0 (Flesh to Worms), +35.00%.
		private const uint DecompositionKind = 53075;
		private const uint FleshToWormsLine = 41000;
		private const uint RankFivePercent = 3500;

		// The two readers split one scan, so they can never disagree about where a record is - only about which side
		// of the stat-id bound it falls on. A record is returned by exactly one of them.
		[Fact]
		public void SpellModifier_IsRejectedByParse_AndReturnedByParseRejected()
		{
			var body = Template(DecompositionKind, FleshToWormsLine, RankFivePercent);

			Assert.Empty(ItemTemplateStatModifiers.Parse(body));

			var rejected = ItemTemplateStatModifiers.ParseRejected(body);
			var mod = Assert.Single(rejected);
			Assert.Equal(DecompositionKind, mod.Kind);
			Assert.Equal(FleshToWormsLine, mod.StatId); // the "stat id" slot carries the spell line for this kind
			Assert.Equal(RankFivePercent, mod.WireAmount);
		}

		// ...and the converse, so the split cannot silently swallow a real stat modifier: HealthMax (1) is well inside
		// the bound and must still come out of Parse alone.
		[Fact]
		public void StatModifier_IsReturnedByParse_AndNotByParseRejected()
		{
			var body = Template(53045, 1, 7500);

			var mod = Assert.Single(ItemTemplateStatModifiers.Parse(body));
			Assert.Equal(1u, mod.StatId);
			Assert.Equal(7500u, mod.WireAmount);
			Assert.Empty(ItemTemplateStatModifiers.ParseRejected(body));
		}

		// The region is not 4-byte aligned to the body in most templates (a feat's name and description are
		// variable-length), so the scan is byte-granular. A word-aligned scan finds nothing at an odd offset - which
		// is exactly how Decomposition's record hid.
		[Theory]
		[InlineData(0)]
		[InlineData(1)]
		[InlineData(2)]
		[InlineData(3)]
		public void SpellModifier_IsFound_AtAnyByteAlignment(int pad)
		{
			var body = Template(DecompositionKind, FleshToWormsLine, RankFivePercent, pad);

			var mod = Assert.Single(ItemTemplateStatModifiers.ParseRejected(body));
			Assert.Equal(FleshToWormsLine, mod.StatId);
			Assert.Equal(RankFivePercent, mod.WireAmount);
		}

		// The five ranks of Decomposition, as the client draws them. The magnitude is hundredths of a percent, which
		// is what makes rank 5 read +35% rather than +3500%.
		[Theory]
		[InlineData(1000, 10)]
		[InlineData(1500, 15)]
		[InlineData(2000, 20)]
		[InlineData(2500, 25)]
		[InlineData(3500, 35)]
		public void SpellDamage_IsScaledByTheFeatPercent(int hundredths, int expectedPercent)
		{
			const ushort level = 80;
			var unboosted = CombatResolver.ResolveSpellDamage(0, level);
			var boosted = CombatResolver.ResolveSpellDamage(0, level, 0, hundredths);

			Assert.Equal(unboosted + (unboosted * expectedPercent / 100), boosted);
		}

		// No feat, no change - the overwhelmingly common case, and the one a regression would hide in.
		[Fact]
		public void SpellDamage_IsUnchanged_WithoutAFeatBoost()
		{
			Assert.Equal(CombatResolver.ResolveSpellDamage(0, 40), CombatResolver.ResolveSpellDamage(0, 40, 0, 0));
		}

		// The boost scales the whole result, Magic Damage included: the client's wording is "Increase to damage or
		// healing on <spell>", not a bonus to one term.
		[Fact]
		public void SpellDamage_BoostCoversTheMagicDamageTerm()
		{
			var withGear = CombatResolver.ResolveSpellDamage(0, 80, 500);
			var boosted = CombatResolver.ResolveSpellDamage(0, 80, 500, 3500);

			Assert.Equal(withGear + (withGear * 35 / 100), boosted);
		}

		// A negative (debuff-shaped) modifier can take a hit down but never to zero - a spell that stops dealing
		// damage would read in-client as a miss.
		[Fact]
		public void SpellDamage_FloorsAtOne_UnderAHugeNegative()
		{
			Assert.Equal(1, CombatResolver.ResolveSpellDamage(0, 80, 0, -1_000_000));
		}

		// A spell the character has no feat for reads 0, and so does a spell id of 0 - no accidental default boost.
		[Fact]
		public void SpellBoost_IsZero_ForAnUnboostedSpell()
		{
			var entity = new GameServer.World.WorldEntity(1, 1, true, "NecroL80", null!)
			{
				SpellBoosts = new Dictionary<uint, int> { [144677] = 3500 }
			};

			Assert.Equal(3500, entity.SpellBoost(144677));
			Assert.Equal(0, entity.SpellBoost(144676));
			Assert.Equal(0, entity.SpellBoost(0));
		}

		// A minimal template body: the 12-byte RDB-Id header, `pad` bytes of filler to shift the alignment, then one
		// 10-word modifier record `[kind][0][4][0][1][0][2][9][ref][value]`. The parser needs a zero word before the
		// signature, which the record's own second word supplies.
		private static byte[] Template(uint kind, uint reference, uint value, int pad = 0)
		{
			var words = new[] { kind, 0u, 4u, 0u, 1u, 0u, 2u, 9u, reference, value };
			var body = new byte[12 + pad + (words.Length * 4) + 4];
			for (var i = 0; i < words.Length; i++)
				BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12 + pad + (i * 4)), words[i]);

			return body;
		}
	}
}
