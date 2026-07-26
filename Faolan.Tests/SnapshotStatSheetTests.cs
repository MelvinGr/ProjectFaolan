using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Progression;
using Xunit;

namespace Faolan.Tests
{
	// #447's acceptance test: the snapshot stat sheet has ONE author, so a modelled stat's value reaches the wire
	// whatever the character's provenance. The bug this exists to prevent is not hypothetical - setting the GM Level
	// stat on the static fresh list did nothing for a single real login, because imported characters never touched
	// that list, and nothing anywhere said so.
	public class SnapshotStatSheetTests
	{
		// A captured sheet, of the exact size the snapshot requires, with a recognisable value in every slot so an
		// override is visible as a change rather than as a coincidence.
		private static IReadOnlyList<(CharacterStatId Id, uint Value)> Captured()
		{
			return [.. SubClassStatBlock.FreshSheet().Select(p => ((CharacterStatId)p.Id, 0xDEADu))];
		}

		private static Character Char80(AccountType type = AccountType.Normal)
		{
			return new Character
			{
				Level = 80,
				Experience = 4242,
				Account = new Account { Type = type }
			};
		}

		public static TheoryData<bool> Provenance => [true, false];

		// The whole point: every override lands, for an imported character AND a freshly-created one.
		[Theory]
		[MemberData(nameof(Provenance))]
		public void EveryOverride_Lands_WhateverTheProvenance(bool imported)
		{
			var sheet = SnapshotStatSheet.For(Char80(AccountType.Administrator), 0x0802ABCD, imported ? Captured() : null);

			Assert.Equal(0x0802ABCDu, Value(sheet, CharacterStatId.PlayfieldToken));
			Assert.Equal(GmRoles.All, Value(sheet, CharacterStatId.GmLevel));
			Assert.Equal(80u, Value(sheet, CharacterStatId.Level));
			Assert.Equal(4242u, Value(sheet, CharacterStatId.Exp));
			Assert.Equal(LevelingResolver.XpForNextLevel(80), Value(sheet, CharacterStatId.MaxExp));
			Assert.Equal(0u, Value(sheet, CharacterStatId.ExpCurrentLevelBase));
		}

		// An override writes EVERY occurrence, not just the first. Level and the XP stats appear in both StatList#1
		// and StatList#2, and half-writing one is exactly what leaves a sheet internally inconsistent.
		[Theory]
		[MemberData(nameof(Provenance))]
		public void AnOverride_WritesEveryOccurrenceOfItsStat(bool imported)
		{
			var sheet = SnapshotStatSheet.For(Char80(), null, imported ? Captured() : null);

			var levels = sheet.Where(p => p.Id == (uint)CharacterStatId.Level).ToList();
			Assert.True(levels.Count > 1, "Level appears in both stat lists - the fixture must exercise that");
			Assert.All(levels, p => Assert.Equal(80u, p.Value));
		}

		// A slot the snapshot does NOT derive keeps whatever the baseline gave it. Without this the previous test
		// would also pass on a builder that simply overwrote the whole sheet.
		[Fact]
		public void ANonDerivedSlot_KeepsTheCapturedValue()
		{
			var sheet = SnapshotStatSheet.For(Char80(), null, Captured());

			Assert.Equal(0xDEADu, Value(sheet, CharacterStatId.AttackRating));
			Assert.Equal(0xDEADu, Value(sheet, CharacterStatId.Money));
		}

		// The sheet is always the full size the two StatList_t need - a builder that dropped or appended a pair would
		// throw much later, in FullCharSnapshotSubClass, with the character already half-built.
		[Theory]
		[MemberData(nameof(Provenance))]
		public void TheSheet_IsAlwaysTheFullSize(bool imported)
		{
			Assert.Equal(SubClassStatBlock.SheetSize, SnapshotStatSheet.For(Char80(), null, imported ? Captured() : null).Count);
		}

		// No character (the create-editor / broadcast path) is not an error: the session slots still author, and the
		// character-derived ones keep the baseline rather than reading 0 off a null.
		[Fact]
		public void NoCharacter_StillAuthorsTheSessionSlots()
		{
			var sheet = SnapshotStatSheet.For(null, 0x0802ABCD, null);

			Assert.Equal(0x0802ABCDu, Value(sheet, CharacterStatId.PlayfieldToken));
			Assert.Equal(0u, Value(sheet, CharacterStatId.GmLevel));
			Assert.Equal(1u, Value(sheet, CharacterStatId.Level)); // the skeleton's, untouched
		}

		private static uint Value(List<(uint Id, uint Value)> sheet, CharacterStatId stat)
		{
			return sheet.First(p => p.Id == (uint)stat).Value;
		}
	}
}
