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
		// which is an independent source - the client's initialiser on one side, shipped template bytes on the other.
		//
		// These widths are the TILING measurement (#595): the distance from an element's opening type word to the
		// NEXT element's, inside a record whose own 0x3F1 frame count agrees with the elements found. That is an
		// EXACT width, where the earlier single-element measurement could only give a ceiling (an element at the end
		// of a record is bounded by the record, so it swallows any trailing record content). It reaches 27 kinds
		// against that pass's 17, and it resolves the two the ceiling left in doubt: `AddSkinnedMesh` tiles at exactly
		// 9 words, so its 12-word "measurement" was trailing record content and not the string-valued key it looked
		// like, and `Charge` never appears as a non-final element, so its lone 8-word reading remains a probable
		// coincidental signature hit rather than an element.
		[Theory]
		[InlineData(53002u, 12)]   // Hit
		[InlineData(53019u, 9)]    // Spell
		[InlineData(53026u, 11)]   // Set
		[InlineData(53030u, 11)]   // ChangeHitPercent
		[InlineData(53031u, 10)]   // ChangeRecastTime
		[InlineData(53032u, 10)]   // AOERadiusModification
		[InlineData(53045u, 10)]   // Modify
		[InlineData(53051u, 9)]    // CastNano
		[InlineData(53054u, 12)]   // SpellStatModifier
		[InlineData(53062u, 12)]   // SpellLineStatModifier
		[InlineData(53067u, 10)]   // ModifyAsRating
		[InlineData(53069u, 10)]   // ChangeLineRecastTime
		[InlineData(53070u, 14)]   // AddSpellLineProc
		[InlineData(53074u, 10)]   // LineAOERadiusModification
		[InlineData(53075u, 11)]   // ChangeLineHitPercent
		[InlineData(53099u, 12)]   // TargetSpellLineStatModifier
		[InlineData(53110u, 10)]   // TempChangeSkill
		[InlineData(53139u, 10)]   // SetFlag
		[InlineData(53162u, 10)]   // NanoResistance
		[InlineData(53176u, 10)]   // DisplayLearnAbility
		[InlineData(53182u, 12)]   // SpecialAbility
		[InlineData(53189u, 12)]   // AddPercentSkillModifier
		[InlineData(53209u, 11)]   // UpgradeCombo
		[InlineData(53211u, 14)]   // AddSpellProc
		[InlineData(53212u, 13)]   // AddComboProc
		[InlineData(53213u, 14)]   // AddProc
		[InlineData(53220u, 13)]   // InterpolatedModify
		[InlineData(53240u, 9)]    // AddSkinnedMesh
		[InlineData(53250u, 14)]   // ChangeInheritedWeaponAttack
		public void ElementWidthMatchesTheWidthMeasuredInTheRdb(uint id, int measuredWords)
		{
			var type = GameActionTypes.Get(id)!;
			Assert.True(type.IsFixedWidth, $"{type.Name} carries a text slot, so it has no fixed width");
			Assert.Equal(measuredWords, type.WidthWords);
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

		// `8 + len(keyList)` describes an element whose every slot is a u32, and key 209 is TEXT - `[u32 byteLength]
		// [bytes, the NUL included]`. Shape taken from feat 12362, where a `HeadText` element measures 47 bytes:
		// 32 header + 4 + "Dimach\0" + the one u32 slot after it. A width that is not a multiple of four is the tell,
		// and no fixed-width layout can produce one. The element must still be LOCATED - it is what bounds its
		// neighbour's width - but it carries no positional values, because reading them would take the string's
		// length for the first slot (#595, follow-up #596).
		[Fact]
		public void AnElementWithATextSlotIsLocatedButNotReadPositionally()
		{
			var body = new byte[96];
			void W(int at, uint v)
			{
				BitConverter.GetBytes(v).CopyTo(body, at);
			}

			// [HeadText][0][4][0][1][0][2][9][u32 7]["Dimach\0"][u32 3]  then a `Modify` element right after it.
			W(4, 53057);
			W(12, 4);
			W(20, 1);
			W(28, 2);
			W(32, 9);
			W(36, 7);
			"Dimach\0"u8.ToArray().CopyTo(body, 40);
			W(47, 3);
			W(51, 53045);
			W(59, 4);
			W(67, 1);
			W(75, 2);
			W(79, 9);
			W(83, 0x324);
			W(87, 12);

			var elements = ItemTemplateStatModifiers.ParseElements(body);
			Assert.Equal(2, elements.Count);
			Assert.Equal(53057u, elements[0].Kind);
			Assert.Empty(elements[0].Values);
			Assert.Null(elements[0].Value(GameActionTypes.KeyValue));

			// 47 bytes to the next element - the measurement the report makes, and the reason the model needs the
			// exception rather than a wider fixed width.
			Assert.Equal(47, elements[1].Offset - elements[0].Offset);
			Assert.Equal(53045u, elements[1].Kind);
			Assert.Equal(0x324u, elements[1].Value(GameActionTypes.KeyStat));
			Assert.Equal(12u, elements[1].Value(GameActionTypes.KeyValue));
		}

		// The shipped-data anomaly behind #595, in the shape feat `Swift Pathing` (5031395..5031401) carries it: two
		// elements wearing the `SpellStatModifier` type word but only TEN words wide, tiling the record end to end.
		// The payload is a plain `(Stat, Value)` pair - Ride Skill and Player Flags, byte-identical to what the
		// `Basic Riding` family (115944..115953) carries under the CORRECT `Modify` (53045) and `SetFlag` (53139)
		// type words - so the type word is stale, not the width model.
		//
		// What this locks is the READER's behaviour on it: both elements are found (the second one has fewer bytes
		// after it than its key list wants, and dropping it would hide the anomaly), and the first one's `Value` slot
		// lands on the next element's type id - which is exactly what `RdbFeatImporter.ParseSpellStatModifiers`
		// rejects on, rather than importing a GameAction type id as a magnitude.
		[Fact]
		public void AnElementNarrowerThanItsKeyListIsStillFound()
		{
			var body = new byte[80];
			void W(int word, uint v)
			{
				BitConverter.GetBytes(v).CopyTo(body, word * 4);
			}

			foreach (var at in (int[]) [0, 10])
			{
				W(at + 0, 53054);
				W(at + 2, 4);
				W(at + 4, 1);
				W(at + 6, 2);
				W(at + 7, 9);
			}

			W(8, 238);   // Ride Skill
			W(9, 5);
			W(18, 864);  // Player Flags
			W(19, 5);

			var elements = ItemTemplateStatModifiers.ParseElements(body);
			Assert.Equal(2, elements.Count);
			Assert.Equal(40, elements[1].Offset - elements[0].Offset);

			// Read by key, the first element's Value slot is the SECOND element's opening word.
			Assert.Equal(238u, elements[0].Value(GameActionTypes.KeySpell));
			Assert.Equal(5u, elements[0].Value(GameActionTypes.KeyStat));
			Assert.Equal(53054u, elements[0].Value(GameActionTypes.KeyValue));
			Assert.NotNull(GameActionTypes.Get(elements[0].Value(GameActionTypes.KeyValue)!.Value));

			// The trailing one simply runs out of body, and is reported short rather than dropped.
			Assert.Equal([864u, 5u], elements[1].Values);
			Assert.Null(elements[1].Value(GameActionTypes.KeyValue));
		}
	}
}
