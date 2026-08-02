using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// The snapshot carries TWO 0x3F1-framed StatList_t (client reader FUN_00797440 calls FUN_0139d450 twice, on
	// this+0x74 and this+0x78), and BOTH are built from one stat sheet of exactly SheetSize pairs.
	//
	// A wrong-sized sheet used to fall back to the baseline skeleton SILENTLY, which would ship a level-80 character
	// with skeleton vitals and no error anywhere. It throws now, and these tests pin that: loud on corruption, still
	// quiet on the legitimate create path.
	public class StatSheetSizeTests
	{
		private const uint BeachStarterQuestId = 10238;
		private const uint TemplateInstance = 0x0802E5D4;

		// A freshly created character - with a CLASS, because vitals are derived from (Class, Level) and there is
		// deliberately no fallback curve for an unset class (a 0 is a corrupt row, not a new character). This is what
		// DatabaseRepository.CreateCharacter allocates.
		private static Character Fresh()
		{
			return new Character { Class = ConanClass.Barbarian, Level = 1 };
		}

		// A character with NO stored sheet is the create path, not corruption: it must still build the skeleton.
		[Fact]
		public async Task EmptyStatSheet_StillBuilds_TheBaselineSkeleton()
		{
			var def = await LoadDefinition();
			Assert.NotNull(def);

			var built = FullCharSnapshotSubClassCodec.Build(SnapshotTails.FromScratch(def, Fresh(), TemplateInstance, null, [], []));
			Assert.NotEmpty(built);
		}

		// The two lists must account for the whole sheet - if either half is resized without the other, every imported
		// character silently starts throwing instead of shipping.
		[Fact]
		public void SheetSize_IsExactlyTheTwoStatLists()
		{
			Assert.Equal(SubClassStatBlock.SheetSize, SubClassStatBlock.StatList1Size + SubClassStatBlock.StatList2Size);
		}

		[Fact]
		public async Task WrongSizedStatSheet_Throws_RatherThanSilentlyUsingBaseline()
		{
			var def = await LoadDefinition();
			Assert.NotNull(def);

			// One pair short of a full sheet - the shape a partial CharacterStats import produces.
			var partial = new List<(CharacterStatId Id, uint Value)>();
			for (var i = 0; i < SubClassStatBlock.SheetSize - 1; i++)
				partial.Add((CharacterStatId.Level, 1));

			var ex = Assert.Throws<InvalidOperationException>(() => SnapshotTails.FromScratch(def, Fresh(), TemplateInstance, null, [], partial));

			Assert.Contains($"{SubClassStatBlock.SheetSize}", ex.Message);
		}

		private static async Task<QuestDefinition?> LoadDefinition()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestPaths.FaolanDb)); // shared fixture - read-only so a future edit can't mutate it
			return await db.QuestDefinitions.Include(q => q.TextRefs).Include(q => q.Goals).FirstOrDefaultAsync(q => q.QuestId == BeachStarterQuestId);
		}
	}
}
