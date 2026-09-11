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
			foreach (var line in Rows(TestPaths.Repo("docs", "re", "gameaction-keylists.tsv")))
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

		// `widthWords = 8 + keyCount`, on all 260 rows with no exception - so the descriptor's own width
		// states how many keys the type has, and it is readable statically even where the key array is not.
		//
		// That also settles the 57 blank `keys` cells, which a static read might be thought to miss because the
		// descriptor table is bss and filled at runtime: they need no live dump. Every blank row reads width exactly 8,
		// the header-only size, and the blank set is exactly the width-8 set. A row whose key array was hidden but
		// non-empty would show width > 8 with no keys beside it, and there is not one. So those 57 types genuinely
		// carry no fields - `StartFlymode` (53138) among them, which is what makes the empty key list `.fly` sends
		// correct rather than an assumption.
		[Fact]
		public void DescriptorWidth_IsEightPlusTheKeyCount_SoABlankKeyListMeansNoKeys()
		{
			var widths = new List<(uint Id, int Width, int Keys)>();
			foreach (var line in Rows(TestPaths.Repo("docs", "re", "gameaction-keylists.tsv")))
			{
				var f = line.Split('	');
				widths.Add((uint.Parse(f[0]), int.Parse(f[4]), f[5].Length == 0 ? 0 : f[5].Split(';').Length));
			}

			var broken = widths.Where(w => w.Width != 8 + w.Keys).Select(w => $"type {w.Id}: width {w.Width}, {w.Keys} key(s)").ToList();
			Assert.True(broken.Count == 0, "widthWords must be 8 + keyCount:\n  " + string.Join("\n  ", broken));

			// ...and the blank rows are exactly the width-8 ones, which is what turns "we could not read them" into
			// "there is nothing to read".
			Assert.Equal(widths.Count(w => w.Keys == 0), widths.Count(w => w.Width == 8));
			Assert.Empty(GameActionTypes.Get(53138)!.Keys); // StartFlymode - the .fly element
		}

		// A descriptor's key list holds an index; the element reader resolves it through the client's key table
		// (`DAT_025073c0`, built by `FUN_00897bd0`) to the real property key, which is an ordinary stat id. The
		// `statKeys` column carries that resolution, and the mapping is a property of the index - so the same index
		// must resolve the same way on every one of the 260 types. A re-dump that mis-read the table would show up
		// here as one index with two keys, rather than as silently renamed properties.
		[Fact]
		public void EveryKeyIndexResolvesToOneStatKeyAcrossAllTypes()
		{
			var byIndex = new Dictionary<int, string>();
			foreach (var line in Rows(TestPaths.Repo("docs", "re", "gameaction-keylists.tsv")))
			{
				var f = line.Split('\t');
				if (f[5].Length == 0)
					continue;

				var keys = f[5].Split(';');
				var stats = f[6].Split(';');
				Assert.Equal(keys.Length, stats.Length);
				for (var i = 0; i < keys.Length; i++)
				{
					Assert.NotEmpty(stats[i]);
					var index = int.Parse(keys[i]);
					if (byIndex.TryGetValue(index, out var seen))
						Assert.Equal(seen, stats[i]);
					else
						byIndex[index] = stats[i];
				}
			}

			// The two the mounted-combat question turns on: the lance charge is the only action that asks
			// whether it may be used from the saddle, and it asks through stat 0x4FD `GameAction_CanUseIfMounted`.
			Assert.Equal("0x4FD", byIndex[245]);

			// And the pair the recast RE already published by their resolved keys, which is what says the table is
			// read right rather than merely read consistently (docs/re/gameactionlist-grammar.md).
			Assert.Equal("0x463", byIndex[88]);
			Assert.Equal("0x2F9", byIndex[39]);
		}

		// The id space the client asserts on every read: `i_nGameAction >= e_Type_GC_GameAction && < _End`,
		// compiled as `(id - 0xCF09) <= 0x11D` (docs/re/gameactionlist-grammar.md).
		[Fact]
		public void EveryTypeIsInsideTheIdSpaceTheClientAsserts()
		{
			foreach (var type in GameActionTypes.All)
				Assert.InRange(type.Id, GameActionTypes.FirstId, GameActionTypes.LastId);
		}

		// The catalog is complete, and this is what says so. The descriptor table is a flat array indexed by
		// the id, so every id in the range is either registered or a hole - there is no third state. Asserting that
		// the two sets partition the range is what turns "the dump found 260" into "the client has 260": if a later
		// re-dump recovers one of the 26, the tsv gains a row and this fails until `UnregisteredIds` gives it up, and
		// an id that fell out of the tsv fails the same way rather than quietly becoming "retired".
		//
		// Without it, an unregistered kind in shipped data (53044) reads as a gap in our recovery rather than as
		// what it is: content the client itself refuses to stream.
		[Fact]
		public void TheRegisteredTypesAndTheRetiredIdsPartitionTheIdSpace()
		{
			var registered = GameActionTypes.All.Select(t => t.Id).ToHashSet();
			Assert.Empty(registered.Intersect(GameActionTypes.UnregisteredIds));

			var span = Enumerable.Range((int)GameActionTypes.FirstId, (int)(GameActionTypes.LastId - GameActionTypes.FirstId + 1))
				.Select(i => (uint)i)
				.ToHashSet();
			Assert.Equal(span.Count, registered.Count + GameActionTypes.UnregisteredIds.Count);
			Assert.Empty(span.Except(registered).Except(GameActionTypes.UnregisteredIds));
		}

		// The whole point of the key list: an element carries exactly one u32 per key after a fixed 8-word prefix,
		// so the width is knowable rather than assumed. Measured against the RDB (`--report feat-effect-widths`),
		// which is an independent source - the client's initialiser on one side, shipped template bytes on the other.
		//
		// These widths are the tiling measurement: the distance from an element's opening type word to the
		// next element's, inside a record whose own 0x3F1 frame count agrees with the elements found. That is an
		// exact width, where the earlier single-element measurement could only give a ceiling (an element at the end
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

		// The slot constants the modifier readers address by. Naming a slot by key rather than by position is the
		// whole reason a `SpellStatModifier` can be read at all: it puts the spell first and the stat second, so a
		// positional read takes the spell for a stat id and the stat id for a magnitude.
		[Fact]
		public void TheModifierSlotKeysSitWhereTheKindSaysTheyDo()
		{
			var spellMod = GameActionTypes.Get(53054u)!;
			Assert.Equal(0, spellMod.SlotOf(GameActionTypes.KeySpell));
			Assert.Equal(1, spellMod.SlotOf(GameActionTypes.KeyStat));
			Assert.Equal(2, spellMod.SlotOf(GameActionTypes.KeyValue));
			Assert.Equal(3, spellMod.SlotOf(GameActionTypes.KeyAmount));

			// Its per-line twin is the same record with the spell slot replaced by a line slot - which is what makes
			// key 1 the line, on this type and on every other `...Line...` one.
			var lineMod = GameActionTypes.Get(53062u)!;
			Assert.Equal(0, lineMod.SlotOf(GameActionTypes.KeyLine));
			Assert.Equal(-1, lineMod.SlotOf(GameActionTypes.KeySpell));
			Assert.Equal(spellMod.Keys.Skip(1), lineMod.Keys.Skip(1));

			// A plain stat modifier is (Stat, Value) and nothing else - not the fixed ten words a width-blind reader
			// assumes for every kind.
			Assert.Equal([GameActionTypes.KeyStat, GameActionTypes.KeyValue], GameActionTypes.Get(53045u)!.Keys);
		}

		// A one-key element has no second payload word. Taking one anyway reads the next element's type id, which
		// makes a `Spell` grant look as if it "repeats its own kind".
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

		// `8 + len(keyList)` describes an element whose every slot is a u32, and keys 209/210 are text - `[u16
		// byteLength][bytes]`. A text slot shifts every slot after it by an odd number of bytes, which is both
		// why such an element has no fixed width and why it has to be walked rather than indexed: a positional read
		// past the string takes its length for the next slot's value. Shape taken from spell 4609400 "Path of the
		// Enlightened", whose `SetMorphValue` element reads `[u16 3]["arm"][u32 -10]` and tiles at 41 bytes.
		[Fact]
		public void AnElementWithATextSlotIsWalkedSoTheSlotsAfterItStayAligned()
		{
			var body = new byte[96];
			void W(int at, uint v)
			{
				BitConverter.GetBytes(v).CopyTo(body, at);
			}

			// [SetMorphValue][0][4][0][1][0][2][9][u16 3]["arm"][u32 -10]  then a `Modify` element right after it.
			W(4, 53010);
			W(12, 4);
			W(20, 1);
			W(28, 2);
			W(32, 9);
			BitConverter.GetBytes((ushort)3).CopyTo(body, 36);
			"arm"u8.ToArray().CopyTo(body, 38);
			W(41, unchecked((uint)-10));
			W(45, 53045);
			W(53, 4);
			W(61, 1);
			W(69, 2);
			W(73, 9);
			W(77, 0x324);
			W(81, 12);

			var elements = ItemTemplateStatModifiers.ParseElements(body);
			Assert.Equal(2, elements.Count);
			Assert.Equal(53010u, elements[0].Kind);

			// The string itself, and - the point of walking - the u32 after it, read at its real offset.
			Assert.Equal("arm", elements[0].Text(GameActionTypes.KeyEventString));
			Assert.Equal(unchecked((uint)-10), elements[0].Value(GameActionTypes.KeyValue));

			// A text slot's Value is its byte length - what the u16 actually holds - not the string.
			Assert.Equal(3u, elements[0].Value(GameActionTypes.KeyEventString));
			Assert.Null(elements[0].Text(GameActionTypes.KeyValue));

			// 41 bytes to the next element - the measurement the report makes, and what settles the length word's
			// width: the u32 form would put this element's end two bytes past where its neighbour begins.
			Assert.Equal(41, elements[1].Offset - elements[0].Offset);
			Assert.Equal(41, elements[0].WidthBytes);
			Assert.Equal(53045u, elements[1].Kind);
			Assert.Equal(0x324u, elements[1].Value(GameActionTypes.KeyStat));
			Assert.Equal(12u, elements[1].Value(GameActionTypes.KeyValue));
		}

		// The shipped-data anomaly, in the shape the feat `Swift Pathing` (5031395..5031401) carries it: two
		// elements wearing the `SpellStatModifier` type word but only ten words wide, tiling the record end to end.
		// The payload is a plain `(Stat, Value)` pair - Ride Skill and Player Flags, byte-identical to what the
		// `Basic Riding` family (115944..115953) carries under the correct `Modify` (53045) and `SetFlag` (53139)
		// type words - so the type word is stale, not the width model.
		//
		// What this locks is the reader's behaviour on it: both elements are found (the second one has fewer bytes
		// after it than its key list wants, and dropping it would hide the anomaly), and the first one's `Value` slot
		// lands on the next element's type id - which is exactly what `ItemTemplateStatModifiers.ParseSpellStatModifiers`
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

			// Read by key, the first element's Value slot is the second element's opening word.
			Assert.Equal(238u, elements[0].Value(GameActionTypes.KeySpell));
			Assert.Equal(5u, elements[0].Value(GameActionTypes.KeyStat));
			Assert.Equal(53054u, elements[0].Value(GameActionTypes.KeyValue));
			Assert.NotNull(GameActionTypes.Get(elements[0].Value(GameActionTypes.KeyValue)!.Value));

			// The trailing one simply runs out of body, and is reported short rather than dropped.
			Assert.Equal([864u, 5u], elements[1].Values);
			Assert.Null(elements[1].Value(GameActionTypes.KeyValue));
		}

		// The tsv carries a `#` provenance header - where the dump's window comes from and why 58 rows have no keys
		//. Skipping comments as well as the column line keeps that documentable without the gate seeing it.
		private static IEnumerable<string> Rows(string path)
		{
			return File.ReadAllLines(path).Where(l => !l.StartsWith('#')).Skip(1);
		}
	}
}
