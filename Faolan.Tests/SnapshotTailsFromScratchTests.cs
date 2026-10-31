using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Progression;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// The whole beach 0x737A6DF9 tails build from scratch - no captured-tails template. BuildFromScratch
	// assembles the three regions (generated quest portion + the game-static access-window catalog asset + the code
	// stat skeleton) and reproduces BeachTailsTemplate.bin byte-for-byte for the captured beach character + instance.
	// Proving this means the .bin is not needed to author a beach world-entry snapshot's tails.
	public class SnapshotTailsFromScratchTests
	{
		private const uint BeachStarterQuestId = 10238;

		// A character's vitals ceiling with no gear and the level-1 attribute base - which is what every character in
		// this file is (freshly-built, no equipment). The world-entry path passes the live EntityVitals instead.
		private static CharacterMaxStats MaxOf(Character character)
		{
			return character.MaxVitals(VitalAttributes.LevelOneBase);
		}

		private const uint TemplateInstance = 0x02165EFF;
		private static readonly string DbPath = TestPaths.FaolanDb;
		private static string TemplatePath => TestPaths.Asset("BeachTailsTemplate.bin");

		// The template is Strie (an Assassin/Rogue: HP 98 base 90, Mana 0, Stamina 236 base 230). When the whole
		// per-class stat block is authored for an Assassin at L1 it must reproduce that block exactly - i.e. the
		// production beach flip stays byte-identical to the proven captured path for the baseline class, and the
		// per-class authoring only diverges for a different class (Necro etc., the intended #4 change). Guards against
		// the SnapshotVitals/ApplyClassVitals slot map drifting away from the captured Rogue values.
		[Fact]
		public async Task BuildFromScratch_AuthorsAssassinBlock_StillReproducesTheTemplate()
		{
			var def = await LoadDefinition(BeachStarterQuestId);
			Assert.NotNull(def);

			var strie = new Character
			{
				Level = 1,
				// Strie is a fresh level-1 character with no experience. Word [80] is MaxExp's slot, not experience, and
				// its 100 is the level-1 to-next-level cost - authoring MaxExp from the curve reproduces that byte with
				// the character's real, zero, experience.
				Experience = 0,
				Class = ConanClass.Assassin,
				// Strie is female, and the sheet's Sex slot is authored from the character rather than carried at
				// the captured 3 - so the golden states who the template's character is instead of inheriting it.
				Sex = Sex.Female,
				// ...and whose account it is: the sheet's PlayerId is the account's, not the character's.
				AccountId = 0x59EA506E
			};
			var built = FullCharSnapshotSubClassCodec.Build(SnapshotTails.FromScratch(def, strie, MaxOf(strie), TemplateInstance));

			var template = await File.ReadAllBytesAsync(TemplatePath, TestContext.Current.CancellationToken);
			Assert.True(template.SequenceEqual(built), "authoring the per-class block for an Assassin L1 must still equal the captured Strie template byte-for-byte");
		}

		// The sheet's vitals come from the character, whatever the sheet's provenance. A captured stat sheet is
		// a baseline, not an override - exactly like the Level/Exp/GmLevel overrides, whose own comment notes a
		// captured level is "stale the moment it dings". Guarding it on `!imported` would let a character with rows in
		// CharacterStats render its capture-time health for ever: the same 1270 at level 10 and at level 40 in-client,
		// while armour mitigation moves with the level and proves the client knows.
		//
		// Nothing is lost by overriding: vitals are derived from (Class, Level), so an imported character and a
		// created one of the same class and level report the same numbers.
		[Fact]
		public async Task BuildFromScratch_ImportedSheet_StillTakesItsVitalsFromTheCharacter()
		{
			var def = await LoadDefinition(BeachStarterQuestId);
			Assert.NotNull(def);

			var character = new Character
			{
				Level = 7,
				Experience = 98760,
				Class = ConanClass.Assassin
			};

			// A captured sheet carrying a different health from the character's.
			var captured = SubClassStatBlock.FreshSheet()
				.Select(p => ((CharacterStatId)p.Id, p.Value))
				.ToList();

			var fromCapture = SnapshotTails.FromScratch(def, character, MaxOf(character), 0x0802ABCD, statSheet: captured);
			var fromScratch = SnapshotTails.FromScratch(def, character, MaxOf(character), 0x0802ABCD);

			Assert.Equal(character.MaxVitals(VitalAttributes.LevelOneBase).Health, fromCapture.PostAccessBlock!.CurrentHp);
			Assert.Equal(fromScratch.PostAccessBlock!.CurrentHp, fromCapture.PostAccessBlock.CurrentHp);
			Assert.Equal(fromScratch.PostAccessBlock.GetStat(CharacterStatId.HealthMax), fromCapture.PostAccessBlock.GetStat(CharacterStatId.HealthMax));
		}

		// A dead character's from-scratch tails carry current-health = 0 (the release-window gate)
		// while the max/base HP side stays full - the same shape the retail login-while-dead snapshot has. An alive
		// character with identical stats keeps full current health, so the only difference is the dead flag.
		[Fact]
		public async Task BuildFromScratch_DeadCharacter_ZeroesCurrentHealthKeepsMax()
		{
			var def = await LoadDefinition(BeachStarterQuestId);
			Assert.NotNull(def);

			var alive = new Character
			{
				// A real class: the vitals anchors are per-class and there is no fallback row for an unset one.
				Class = ConanClass.Barbarian,
				Level = 7,
				Experience = 98760,
				IsDead = false
			};
			var dead = new Character
			{
				Class = ConanClass.Barbarian,
				Level = 7,
				Experience = 98760,
				IsDead = true
			};
			const uint live = 0x0802ABCD;

			var aliveSubClass = SnapshotTails.FromScratch(def, alive, MaxOf(alive), live);
			var deadSubClass = SnapshotTails.FromScratch(def, dead, MaxOf(dead), live);

			Assert.Equal(alive.MaxVitals(VitalAttributes.LevelOneBase).Health, aliveSubClass.PostAccessBlock!.CurrentHp); // alive: full current health
			Assert.Equal(0u, deadSubClass.PostAccessBlock!.CurrentHp); // dead: current health = 0 (the gate)

			// Max HP is untouched by death - HealthMax stays full in both.
			Assert.Equal(aliveSubClass.PostAccessBlock.GetStat(CharacterStatId.HealthMax), deadSubClass.PostAccessBlock.GetStat(CharacterStatId.HealthMax));
			Assert.True(deadSubClass.PostAccessBlock.GetStat(CharacterStatId.HealthMax) > 0u, "dead snapshot keeps the max-health side populated");
			return;

		}

		[Fact]
		public async Task BuildFromScratch_ReAddressesInstance()
		{
			var def = await LoadDefinition(BeachStarterQuestId);
			const uint live = 0x0802ABCD;
			var built = FullCharSnapshotSubClassCodec.Build(SnapshotTails.FromScratch(def, null!, null!, live));

			// the template instance must be gone, the live one present (raw big-endian)
			var needleOld = new byte[] { 0x02, 0x16, 0x5E, 0xFF };
			var needleNew = new byte[] { 0x08, 0x02, 0xAB, 0xCD };
			Assert.False(Contains(built, needleOld), "template instance should be re-addressed away");
			Assert.True(Contains(built, needleNew), "live instance should be present");
		}

		[Fact]
		public async Task BuildFromScratch_ReproducesTheTemplate_ByteForByte()
		{
			var def = await LoadDefinition(BeachStarterQuestId);
			Assert.NotNull(def);

			// character = null -> the stat skeleton stays at its (Strie) baseline, instance = the template's, so no
			// re-addressing: the assembly must equal the captured tails exactly.
			var built = FullCharSnapshotSubClassCodec.Build(SnapshotTails.FromScratch(def, null!, null!, TemplateInstance));

			var template = await File.ReadAllBytesAsync(TemplatePath, TestContext.Current.CancellationToken);
			Assert.Equal(template.Length, built.Length);
			Assert.True(template.SequenceEqual(built), "from-scratch tails must equal BeachTailsTemplate.bin byte-for-byte");
		}

		// The captured-byte tests above only exercise the Strie baseline. The production flip (authoring the tails
		// instead of replaying the captured burst) relies on BuildFromScratch staying structurally valid for any
		// character - a different class/level patches different vitals into the stat block, and the snapshot framing
		// (the in-client hang risk) must survive that. This proves: the assembly re-parses, the three regions keep
		// their boundaries (quest portion = 1292, access-window catalog parses), the live vitals/XP land in the
		// right PostAccessBlock slots, and the round-trip is lossless - for a character that is not the capture.
		[Fact]
		public async Task BuildFromScratch_StaysValidForANonBaselineCharacter()
		{
			var def = await LoadDefinition(BeachStarterQuestId);
			Assert.NotNull(def);

			var character = new Character
			{
				Class = ConanClass.Barbarian,
				Level = 7,
				Experience = 98760
			};
			const uint live = 0x0802ABCD;

			var built = FullCharSnapshotSubClassCodec.Build(SnapshotTails.FromScratch(def, character, MaxOf(character), live));

			// Re-parses without throwing, and is byte-lossless through the typed model.
			var tails = FullCharSnapshotSubClassCodec.Parse(built);
			Assert.True(built.SequenceEqual(FullCharSnapshotSubClassCodec.Build(tails)), "non-baseline from-scratch tails must round-trip losslessly");

			// Region boundaries intact: the quest portion is the fixed beach length, the static access-window catalog parsed.
			Assert.Equal(SnapshotTails.QuestPortionLength, SnapshotPackets.JournalLength(tails) + QuestRegionTrailerLength(tails));
			Assert.NotEmpty(tails.AccessWindowEntries);

			// The live character's vitals/XP landed in the decoded stat slots (the part the production flip changes).
			Assert.NotNull(tails.PostAccessBlock);
			Assert.Equal(character.MaxVitals(VitalAttributes.LevelOneBase).Health, tails.PostAccessBlock!.CurrentHp);
			Assert.Equal(character.MaxVitals(VitalAttributes.LevelOneBase).Mana, tails.PostAccessBlock.CurrentMana);
			Assert.Equal(character.MaxVitals(VitalAttributes.LevelOneBase).Stamina, tails.PostAccessBlock.CurrentStamina);

			// The XP bar is authored as a consistent triple by stat id, in retail's cumulative form:
			// the running total in 0x34, this level's threshold in 0x39 and the next level's in 0x15E (word [80]).
			// The character row's 98,760 is within-level progress, so the running total is that plus level 7's base.
			var level7Base = LevelingResolver.CumulativeXpForLevel(7);
			Assert.Equal(LevelingResolver.CumulativeXpForLevel(8), tails.PostAccessBlock.MaxExp);
			Assert.Equal(level7Base + 98760u, StatValue(tails.PostAccessBlock, CharacterStatId.Exp));
			Assert.Equal(level7Base, StatValue(tails.PostAccessBlock, CharacterStatId.ExpCurrentLevelBase));
		}

		// A stat's value out of the built block, by ID. Asserts the stat is actually there rather than letting
		// GetStat's "absent reads as 0" make an expected-zero assertion pass vacuously.
		private static uint StatValue(SubClassPostAccessBlock block, CharacterStatId stat)
		{
			var value = block.Displayed.Get((uint)stat) ?? block.Sheet.Get((uint)stat);
			Assert.True(value.HasValue, $"{stat} is present in the built block");
			return value!.Value;
		}

		private static bool Contains(byte[] hay, byte[] needle)
		{
			for (var i = 0; i + needle.Length <= hay.Length; i++)
			{
				if (hay.AsSpan(i, needle.Length).SequenceEqual(needle))
					return true;
			}

			return false;
		}

		private static async Task<QuestDefinition> LoadDefinition(uint questId)
		{
			var options = TestDb.Options(DbPath);
			var repo = new DatabaseRepository(new Factory(options));
			return (await repo.GetQuestDefinition(questId))!;
		}

		// The quest portion = the Quest_c journal (the journal region length) + the @609 objective region + the index region.
		private static int QuestRegionTrailerLength(FullCharSnapshotSubClass tails)
		{
			// The @609 objective region is inside the journal records (the journal region length covers it). What trails
			// the active journal is the completed-journal frame (empty 0x3F1 for a beach char) then the quest index.
			return (tails.CompletedQuests == null ? 0 : CompletedQuestJournalRegionCodec.Build(tails.CompletedQuests).Length)
			       + (tails.QuestIndex == null ? 0 : QuestIndexRegionCodec.Build(tails.QuestIndex).Length);
		}

		private sealed class Factory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
		{
			public DatabaseContext CreateDbContext()
			{
				return new DatabaseContext(options);
			}
		}
	}
}
