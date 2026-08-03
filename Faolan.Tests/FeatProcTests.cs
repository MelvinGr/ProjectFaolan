using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Faolan.Importer.Rdb;
using Xunit;

namespace Faolan.Tests
{
	// #593: the last two decodable feat kinds - 53212 `AddComboProc` and 53070 `AddSpellLineProc` - plus the per-SPELL
	// hit-percent kind 53030, the twin of the long-imported 53075.
	//
	// All three were hidden the same way and it is worth stating once: the legacy PAIR view returns payload words 0
	// and 1 whatever they mean. On 53212 that is (key 88, key 236), so the kind read as "a spell template id, also a
	// grant... value 0 throughout" - the zero being the second proc spell, empty on most records, and the COMBO the
	// proc hangs off sitting three slots further along, unseen. These gates are therefore about reading BY KEY.
	//
	// Pure by design: synthetic template bodies, no RDB, so they run from a plain clone.
	public class FeatProcTests
	{
		private const uint ComboProc = 53212;   // [88 self spell, 236 target spell, 187 combo, 117 trigger, 237 amount]
		private const uint SpellLineProc = 53070; // [88, 236, 1 line, 117, 237, 73 flags]
		private const uint LineHitPercent = 53075;
		private const uint SpellHitPercent = 53030;

		// `Touch of Death`: one proc spell on the carrier, hung off a combo. The combo is payload word 2 - the pair
		// view cannot reach it, which is why this kind read as a bare spell grant.
		[Fact]
		public void ComboProc_ReadsTheComboAndTheCarriersProcSpell()
		{
			var proc = Assert.Single(RdbFeatImporter.ParseProcs(Template(ComboProc, [793092, 0, 3137720, 1, 120])));

			Assert.Equal(ComboProc, proc.Kind);
			Assert.Equal(3137720u, proc.ComboId);
			Assert.Null(proc.SpellLineId); // the line slot belongs to the OTHER kind
			Assert.Equal(793092u, proc.SelfSpellId);
			Assert.Null(proc.TargetSpellId); // key 236 empty - an absent proc, not a proc on spell 0
			Assert.Equal(1u, proc.Trigger);
			Assert.Equal(120, proc.Amount);
		}

		// The client renders `AddComboProcSelf` / `AddComboProcOther` / `AddComboProcBoth` by testing keys 88 and 236
		// INDEPENDENTLY, so a record may carry either spell or both. Target-only is 140 of the 560 shipped kind-53212
		// rows - not an edge case, and a parser that required key 88 would drop every one of them.
		[Theory]
		[InlineData(793092u, 0u)]       // Self
		[InlineData(0u, 3453245u)]      // Other
		[InlineData(793092u, 3453245u)] // Both
		public void ComboProc_CarriesWhicheverProcSpellsAreSet(uint self, uint target)
		{
			var proc = Assert.Single(RdbFeatImporter.ParseProcs(Template(ComboProc, [self, target, 3137720, 1, 120])));

			Assert.Equal(self == 0 ? null : self, proc.SelfSpellId);
			Assert.Equal(target == 0 ? null : target, proc.TargetSpellId);
		}

		// The line form is the same payload with the target space swapped - which is why the two share a table, and
		// why the KIND has to be what picks the column.
		[Fact]
		public void SpellLineProc_FillsTheLineTargetInstead()
		{
			var proc = Assert.Single(RdbFeatImporter.ParseProcs(Template(SpellLineProc, [1112456, 0, 29003, 3, 200, 0])));

			Assert.Equal(SpellLineProc, proc.Kind);
			Assert.Null(proc.ComboId);
			Assert.Equal(29003u, proc.SpellLineId);
			Assert.Equal(1112456u, proc.SelfSpellId);
			Assert.Equal(200, proc.Amount);
		}

		// A proc with nothing to cast, or nothing to hang off, is a mis-sliced element rather than a meaningful row.
		[Theory]
		[InlineData(0u, 0u, 3137720u)] // no proc spell at all
		[InlineData(793092u, 0u, 0u)]  // no combo to attach to
		public void Proc_WithoutASpellOrATarget_IsDropped(uint self, uint target, uint combo)
		{
			Assert.Empty(RdbFeatImporter.ParseProcs(Template(ComboProc, [self, target, combo, 1, 120])));
		}

		// 53030 `ChangeHitPercent` is the per-SPELL twin of 53075, the same three slots with the line slot (key 1)
		// swapped for the spell slot (key 187). The target lands in a DIFFERENT column, and the kind is the only
		// thing that says so - the two are indistinguishable by shape.
		[Fact]
		public void SpellHitPercent_TargetsASpellWhereItsLineTwinTargetsALine()
		{
			var perSpell = Assert.Single(RdbFeatImporter.ParseSpellModifiers(Template(SpellHitPercent, [143411, unchecked((uint)-2000), 100])));
			var perLine = Assert.Single(RdbFeatImporter.ParseSpellModifiers(Template(LineHitPercent, [41000, 3500, 1])));

			Assert.Equal((SpellHitPercent, 143411u, -2000), (perSpell.Kind, perSpell.TargetId, perSpell.PercentHundredths));
			Assert.Equal((LineHitPercent, 41000u, 3500), (perLine.Kind, perLine.TargetId, perLine.PercentHundredths));
		}

		// …and the magnitude is SIGNED on both: the catalog carries a -20/-40/-60% ladder, which read unsigned is
		// 4.29 billion.
		[Fact]
		public void SpellHitPercent_MagnitudeIsSigned()
		{
			var mod = Assert.Single(RdbFeatImporter.ParseSpellModifiers(Template(SpellHitPercent, [143420, unchecked((uint)-6000), 1])));

			Assert.Equal(-6000, mod.PercentHundredths);
		}

		// Neither reader may pick up the other's kinds: 53212 and 53030 sit in the same record, and a shape-based
		// parser would file each as the other.
		[Fact]
		public void EachReaderTakesOnlyItsOwnKinds()
		{
			var body = Template((ComboProc, [793092u, 0u, 3137720u, 1u, 120u]), (SpellHitPercent, [143411u, 500u, 1u]), (53045u, [280u, 2000u]));

			Assert.Equal(ComboProc, Assert.Single(RdbFeatImporter.ParseProcs(body)).Kind);
			Assert.Equal(SpellHitPercent, Assert.Single(RdbFeatImporter.ParseSpellModifiers(body)).Kind);
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
