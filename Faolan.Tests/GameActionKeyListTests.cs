using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Faolan.Core.Rdb;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// GameActionTypes mirrors docs/re/gameaction-keylists.tsv, which tools/ghidra/DumpGameActionKeyLists.java
	// recovers from the client's own initialiser. The table is the field grammar of every GameAction element -
	// including the ones stored in RDB templates as feat/spell/item modifier records - so a drift between the C#
	// and the artifact it was generated from would silently mis-read them.
	public class GameActionKeyListTests
	{
		private static IReadOnlyList<(uint Id, string Name, int[] Keys)> Tsv()
		{
			var rows = new List<(uint, string, int[])>();
			foreach (var line in File.ReadAllLines(TestPaths.Repo("docs", "re", "gameaction-keylists.tsv")).Skip(1))
			{
				var f = line.Split('\t');
				var keys = f[5].Length == 0 ? [] : f[5].Split(';').Select(int.Parse).ToArray();
				rows.Add((uint.Parse(f[0]), f[2].Replace("e_Type_GC_GameAction_", ""), keys));
			}

			return rows;
		}

		[Fact]
		public void CatalogMatchesTheRecoveredArtifact()
		{
			var tsv = Tsv();
			Assert.Equal(tsv.Count, GameActionTypes.All.Count);
			foreach (var (id, name, keys) in tsv)
			{
				var type = GameActionTypes.Get(id);
				Assert.NotNull(type);
				Assert.Equal(name, type.Name);
				Assert.Equal(keys, type.Keys);
			}
		}

		// The id space the client asserts on every read: `i_nGameAction >= e_Type_GC_GameAction && < _End`,
		// compiled as `(id - 0xCF09) <= 0x11D` (docs/re/gameactionlist-grammar.md).
		[Fact]
		public void EveryTypeIsInsideTheIdSpaceTheClientAsserts()
		{
			foreach (var type in GameActionTypes.All)
				Assert.InRange(type.Id, 0xCF09u, 0xD026u);
		}

		// The whole point of the key list: an element carries exactly one u32 per key after a fixed 8-word prefix,
		// so the width is knowable rather than assumed. Measured against the RDB (`--report feat-effect-widths`),
		// which is an independent source - the client's initialiser on one side, shipped template bytes on the
		// other. These are the kinds that occur in single-element feat/spell/item records; `Charge` (one record,
		// measured 8) and `AddSkinnedMesh` (measured 12, probably a string-valued key) are the two that disagree
		// and are deliberately absent.
		[Theory]
		[InlineData(53019u, 9)]    // Spell
		[InlineData(53030u, 11)]   // ChangeHitPercent
		[InlineData(53031u, 10)]   // ChangeRecastTime
		[InlineData(53045u, 10)]   // Modify
		[InlineData(53054u, 12)]   // SpellStatModifier
		[InlineData(53062u, 12)]   // SpellLineStatModifier
		[InlineData(53069u, 10)]   // ChangeLineRecastTime
		[InlineData(53075u, 11)]   // ChangeLineHitPercent
		[InlineData(53110u, 10)]   // TempChangeSkill
		[InlineData(53139u, 10)]   // SetFlag
		[InlineData(53162u, 10)]   // NanoResistance
		[InlineData(53182u, 12)]   // SpecialAbility
		[InlineData(53189u, 12)]   // AddPercentSkillModifier
		[InlineData(53209u, 11)]   // UpgradeCombo
		[InlineData(53211u, 14)]   // AddSpellProc
		[InlineData(53213u, 14)]   // AddProc
		[InlineData(53220u, 13)]   // InterpolatedModify
		public void ElementWidthMatchesTheWidthMeasuredInTheRdb(uint id, int measuredWords)
		{
			Assert.Equal(measuredWords, GameActionTypes.Get(id)!.WidthWords);
		}

		// The slot constants the modifier readers address by. Naming a slot by KEY rather than by position is the
		// whole reason a `SpellStatModifier` can be read at all: it puts the spell first and the stat SECOND, so a
		// positional read takes the spell for a stat id and the stat id for a magnitude (#591).
		[Fact]
		public void TheModifierSlotKeysSitWhereTheKindSaysTheyDo()
		{
			var spellMod = GameActionTypes.Get(53054u)!;
			Assert.Equal(0, spellMod.SlotOf(GameActionTypes.KeySpell));
			Assert.Equal(1, spellMod.SlotOf(GameActionTypes.KeyStat));
			Assert.Equal(2, spellMod.SlotOf(GameActionTypes.KeyValue));
			Assert.Equal(3, spellMod.SlotOf(GameActionTypes.KeyAmount));

			// Its per-LINE twin is the same record with the spell slot replaced by a line slot - which is what makes
			// key 1 the line, on this type and on every other `...Line...` one.
			var lineMod = GameActionTypes.Get(53062u)!;
			Assert.Equal(0, lineMod.SlotOf(GameActionTypes.KeyLine));
			Assert.Equal(-1, lineMod.SlotOf(GameActionTypes.KeySpell));
			Assert.Equal(spellMod.Keys.Skip(1), lineMod.Keys.Skip(1));

			// A plain stat modifier is (Stat, Value) and nothing else - the shape the old fixed-10-word reader
			// assumed for every kind.
			Assert.Equal([GameActionTypes.KeyStat, GameActionTypes.KeyValue], GameActionTypes.Get(53045u)!.Keys);
		}

		// A one-key element has no second payload word. The old reader took one anyway and got the NEXT element's
		// type id, which is why a `Spell` grant looked like it "repeats its own kind" (#591).
		[Fact]
		public void AOneKeyElementDoesNotReadIntoTheNextElement()
		{
			var body = new byte[80];
			void W(int word, uint v)
			{
				BitConverter.GetBytes(v).CopyTo(body, word * 4);
			}

			// [Spell][field 0][4][subCount 0][1][0][2][9][spell id]  then the next element's type id.
			W(1, 53019);
			W(2, 0);
			W(3, 4);
			W(4, 0);
			W(5, 1);
			W(6, 0);
			W(7, 2);
			W(8, 9);
			W(9, 145538);
			W(10, 53045);

			var element = Assert.Single(ItemTemplateStatModifiers.ParseElements(body), e => e.Kind == 53019);
			Assert.Equal([145538u], element.Values);
			Assert.Equal(145538u, element.Value(GameActionTypes.KeyValue));
			Assert.Null(element.Value(GameActionTypes.KeyStat));
		}
	}
}
