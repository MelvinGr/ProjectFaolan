using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Rdb;
using Faolan.Importer.Rdb;
using Xunit;

namespace Faolan.Tests
{
	// The feat kinds that were readable only once the modifier record was understood as a GameAction element
	// read by key - 53054 `SpellStatModifier`, 53062 `SpellLineStatModifier` and 53019 `Spell`.
	//
	// Positional reading is what hid all three, and each hid differently: 53054 puts the spell first and the stat
	// second, so its stat id read as "a magnitude that varies for no reason"; 53019 carries one payload word, so its
	// "magnitude" was the next element's type id. These gates are therefore about the key, not about offsets - a test
	// that asserted word positions would pass just as happily on the misread.
	//
	// Pure by design: synthetic template bodies, no RDB, so they run from a plain clone.
	public class FeatSpellStatModifierTests
	{
		private const uint SpellStatModifier = 53054;
		private const uint SpellLineStatModifier = 53062;
		private const uint SpellGrant = 53019;

		// The per-spell form, on the shape `Bombardment` carries: (spell, stat, absolute value, no percent).
		[Fact]
		public void SpellStatModifier_ReadsTargetStatAndAbsoluteValue()
		{
			var body = Template(SpellStatModifier, [144677, (uint)CharacterStatId.AttackRange, 150, 0]);

			var mod = Assert.Single(ItemTemplateStatModifiers.ParseSpellStatModifiers(body));
			Assert.Equal(SpellStatModifier, mod.Kind);
			Assert.Equal(144677u, mod.TargetId);
			Assert.Equal(CharacterStatId.AttackRange, mod.Stat);
			Assert.Equal(150, mod.Value);
			Assert.Null(mod.PercentHundredths); // the slot the record left at 0 is absent, not a zero magnitude
		}

		// The per-line form is the same payload with the target space swapped, which is exactly why the two share a
		// table - and why the kind, not the slot position, has to be what picks the space.
		[Fact]
		public void SpellLineStatModifier_ReadsTheLineTarget()
		{
			var body = Template(SpellLineStatModifier, [41000, (uint)CharacterStatId.ManaCost, 0, unchecked((uint)-5000)]);

			var mod = Assert.Single(ItemTemplateStatModifiers.ParseSpellStatModifiers(body));
			Assert.Equal(SpellLineStatModifier, mod.Kind);
			Assert.Equal(41000u, mod.TargetId);
			Assert.Equal(CharacterStatId.ManaCost, mod.Stat);
			Assert.Null(mod.Value);
			Assert.Equal(-5000, mod.PercentHundredths); // -50.00%, and signed - most of these are reductions
		}

		// `Value` and `Amount` are alternatives, never a pair: the client's tooltip renders the first with the stat's
		// own unit suffix and the second with a hardcoded "%". A record setting both, or neither, is a shape this
		// decode does not cover, and guessing which wins is how an absolute delta becomes a percentage.
		[Theory]
		[InlineData(150u, 2500u)] // both
		[InlineData(0u, 0u)]      // neither
		public void SpellStatModifier_IsRejected_WhenTheMagnitudeSlotsDisagree(uint value, uint amount)
		{
			var body = Template(SpellStatModifier, [144677, (uint)CharacterStatId.ManaCost, value, amount]);

			Assert.Empty(ItemTemplateStatModifiers.ParseSpellStatModifiers(body));
		}

		// The `Swift Pathing` case: an element narrower than its key list says, so a magnitude slot lands
		// on the following element's opening type word. The guard is the type-id range, not the feat - a spell id, a
		// stat id and a magnitude all live nowhere near 0xCF09..0xD026 - and it is counted, because seven rows quietly
		// vanishing is the failure this whole ticket exists to avoid.
		[Fact]
		public void SpellStatModifier_RejectsAndCounts_AMagnitudeThatIsAGameActionTypeId()
		{
			var body = Template(SpellStatModifier, [238, (uint)CharacterStatId.Duration, SpellStatModifier, 0]);

			Assert.Empty(ItemTemplateStatModifiers.ParseSpellStatModifiers(body, out var misSliced));
			Assert.Equal(1, misSliced);
		}

		// A well-formed record must not be counted as mis-sliced - otherwise the counter would report a problem on
		// every import and stop meaning anything.
		[Fact]
		public void SpellStatModifier_CountsNoMisSlice_OnAWellFormedRecord()
		{
			var body = Template(SpellStatModifier, [144677, (uint)CharacterStatId.SplashDamage, 0, 3000]);

			Assert.Single(ItemTemplateStatModifiers.ParseSpellStatModifiers(body, out var misSliced));
			Assert.Equal(0, misSliced);
		}

		// A grant is one payload word, and it must not reach the modifier reader: filing it there would record a spell
		// the feat gives you as a boost to one you already have.
		[Fact]
		public void SpellGrant_ReadsTheSpellTemplateId_AndIsNotAModifier()
		{
			var body = Template(SpellGrant, [4188065]);

			Assert.Equal(4188065u, Assert.Single(RdbFeatImporter.ParseSpellGrants(body)));
			Assert.Empty(ItemTemplateStatModifiers.ParseSpellStatModifiers(body));
		}

		// ...and the converse: a stat modifier is not a grant. The two readers walk the same element list, so nothing
		// but the kind keeps them apart.
		[Fact]
		public void SpellStatModifier_IsNotReadAsAGrant()
		{
			var body = Template(SpellStatModifier, [144677, (uint)CharacterStatId.Duration, 250, 0]);

			Assert.Empty(RdbFeatImporter.ParseSpellGrants(body));
		}

		// The element list is not 4-byte aligned to the body in most templates - a feat's name and description are
		// variable-length - so a word-aligned reader simply cannot see these records.
		[Theory]
		[InlineData(0)]
		[InlineData(1)]
		[InlineData(2)]
		[InlineData(3)]
		public void SpellStatModifier_IsFound_AtAnyByteAlignment(int pad)
		{
			var body = Template(SpellStatModifier, [144677, (uint)CharacterStatId.CastingTimeSeconds, 0, unchecked((uint)-3300)], pad);

			var mod = Assert.Single(ItemTemplateStatModifiers.ParseSpellStatModifiers(body));
			Assert.Equal(-3300, mod.PercentHundredths);
		}

		// Several elements of mixed kinds in one record, which is the ordinary case: a feat that grants a spell and
		// also boosts one. Each reader must take its own and leave the rest, and the sequence must survive - the DB
		// key is (FeatId, Sequence), so a reader that reordered them would renumber the table on every import.
		[Fact]
		public void MixedRecord_SplitsByKind_AndKeepsWireOrder()
		{
			var body = Template(
				(SpellGrant, [4188065u]),
				(SpellStatModifier, [144677u, (uint)CharacterStatId.SpellCriticalChance, 500u, 0u]),
				(SpellLineStatModifier, [41000u, (uint)CharacterStatId.SplashRadius, 200u, 0u]),
				(SpellGrant, [220723u]));

			Assert.Equal([4188065u, 220723u], RdbFeatImporter.ParseSpellGrants(body).ToArray());

			var mods = ItemTemplateStatModifiers.ParseSpellStatModifiers(body);
			Assert.Equal(2, mods.Count);
			Assert.Equal(SpellStatModifier, mods[0].Kind);
			Assert.Equal(CharacterStatId.SpellCriticalChance, mods[0].Stat);
			Assert.Equal(SpellLineStatModifier, mods[1].Kind);
			Assert.Equal(CharacterStatId.SplashRadius, mods[1].Stat);
		}

		// The twelve spell properties the shipped catalog targets must all be named. They are the client's own LDB 615
		// names, and an unnamed one would leave a bare number in a table whose whole point is to be readable - and
		// would silently widen the ConstrainToEnumValues check the two stat tables carry.
		[Fact]
		public void EverySpellPropertyTheCatalogTargets_IsNamedInTheEnum()
		{
			uint[] targeted = [8, 129, 132, 147, 222, 287, 294, 347, 348, 349, 385, 387, 422, 447];

			var unnamed = targeted.Where(id => !Enum.IsDefined((CharacterStatId)id)).ToArray();

			Assert.Empty(unnamed);
		}

		// One element: `[kind][0][4][0][1][0][2][9]` then one word per key of that kind's key list. The parser needs a
		// zero word before the signature, which the element's own second word supplies for every element but the
		// first - hence the leading pad word.
		private static byte[] Template(uint kind, uint[] payload, int pad = 0)
		{
			return Template(pad, (kind, payload));
		}

		private static byte[] Template(params (uint Kind, uint[] Payload)[] elements)
		{
			return Template(0, elements);
		}

		private static byte[] Template(int pad, params (uint Kind, uint[] Payload)[] elements)
		{
			var words = new List<uint> { 0 };
			foreach (var (kind, payload) in elements)
			{
				words.AddRange([kind, 0u, 4u, 0u, 1u, 0u, 2u, 9u]);
				words.AddRange(payload);
			}

			// A trailing word so the last element is wholly inside the body - a truncated one is skipped by design.
			words.Add(0);

			var body = new byte[12 + pad + (words.Count * 4)];
			for (var i = 0; i < words.Count; i++)
				BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12 + pad + (i * 4)), words[i]);

			return body;
		}
	}
}
