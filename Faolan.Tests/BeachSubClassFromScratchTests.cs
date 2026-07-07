using System;
using System.IO;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.GameServer.FullCharSnapshot;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// #186: the WHOLE beach 0x737A6DF9 tails build FROM SCRATCH - no captured-tails template. BuildFromScratch
	// assembles the three regions (generated quest portion + the game-static access-window catalog asset + the code
	// stat skeleton) and reproduces BeachTailsTemplate.bin BYTE-FOR-BYTE for the captured beach character + instance.
	// Proving this means the .bin is no longer needed to author a beach world-entry snapshot's tails.
	public class BeachTailsFromScratchTests
	{
		private const string DbPath = "/home/melvin/Development/C#/Project Faolan/faolan.db";
		private const string TemplatePath = "/home/melvin/Development/C#/Project Faolan/Faolan.Tests/Assets/BeachTailsTemplate.bin";
		private const uint BeachStarterQuestId = 10238;
		private const uint TemplateInstance = 0x02165EFF;

		// The template IS Strie (an Assassin/Rogue: HP 98 base 90, Mana 0, Stamina 236 base 230). When the WHOLE
		// per-class stat block is authored (#4) for an Assassin at L1 it must reproduce that block exactly - i.e. the
		// production beach flip stays BYTE-IDENTICAL to the proven captured path for the baseline class, and the
		// per-class authoring only diverges for a DIFFERENT class (Necro etc., the intended #4 change). Guards against
		// the SnapshotVitals/ApplyClassVitals slot map drifting away from the captured Rogue values.
		[Fact]
		public async Task BuildFromScratch_AuthorsAssassinBlock_StillReproducesTheTemplate()
		{
			var def = await LoadDefinition(BeachStarterQuestId);
			Assert.NotNull(def);

			var strie = new Character
			{
				Level = 1,
				Experience = 10, // the template's captured XP slot [80]=100 (=×10); matching it isolates the VITALS authoring
				Class = Class.Assassin,
				MaxStats = CharacterMaxStats.CreateDefaults(Class.Assassin, 1)
			};
			var built = FullCharSnapshotSubClass.BuildFromScratch(def, strie, TemplateInstance);

			var template = File.ReadAllBytes(TemplatePath);
			Assert.True(template.SequenceEqual(built), "authoring the per-class block for an Assassin L1 must still equal the captured Strie template byte-for-byte");
		}

		// #214 step 2: a DEAD character's from-scratch tails carry current-health = 0 (the release-window gate)
		// while the max/base HP side stays full - the same shape the retail login-while-dead snapshot has. An alive
		// character with identical stats keeps full current health, so the only difference is the dead flag.
		[Fact]
		public async Task BuildFromScratch_DeadCharacter_ZeroesCurrentHealthKeepsMax()
		{
			var def = await LoadDefinition(BeachStarterQuestId);
			Assert.NotNull(def);

			var alive = new Character
			{
				Level = 7,
				Experience = 98760,
				MaxStats = Stats(),
				IsDead = false
			};
			var dead = new Character
			{
				Level = 7,
				Experience = 98760,
				MaxStats = Stats(),
				IsDead = true
			};
			const uint live = 0x0802ABCD;

			var aliveSubClass = FullCharSnapshotSubClass.Deserialize(FullCharSnapshotSubClass.BuildFromScratch(def, alive, live));
			var deadSubClass = FullCharSnapshotSubClass.Deserialize(FullCharSnapshotSubClass.BuildFromScratch(def, dead, live));

			Assert.Equal(4321u, aliveSubClass.PostAccessBlock!.CurrentHp); // alive: full current health
			Assert.Equal(0u, deadSubClass.PostAccessBlock!.CurrentHp); // dead: current health = 0 (the gate)

			// Max/base HP side is untouched by death - the slot [21] base stays full in both.
			Assert.Equal(aliveSubClass.PostAccessBlock.Values[21], deadSubClass.PostAccessBlock.Values[21]);
			Assert.True(deadSubClass.PostAccessBlock.Values[21] > 0u, "dead snapshot keeps the max-health side populated");
			return;

			CharacterMaxStats Stats()
			{
				return new CharacterMaxStats
				{
					Health = 4321,
					Mana = 765,
					Stamina = 543
				};
			}
		}

		[Fact]
		public async Task BuildFromScratch_ReAddressesInstance()
		{
			var def = await LoadDefinition(BeachStarterQuestId);
			const uint live = 0x0802ABCD;
			var built = FullCharSnapshotSubClass.BuildFromScratch(def, null, live);

			// the template instance must be gone, the live one present (raw big-endian)
			var needleOld = new byte[]
			{
				0x02,
				0x16,
				0x5E,
				0xFF
			};
			var needleNew = new byte[]
			{
				0x08,
				0x02,
				0xAB,
				0xCD
			};
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
			var built = FullCharSnapshotSubClass.BuildFromScratch(def, null, TemplateInstance);

			var template = File.ReadAllBytes(TemplatePath);
			Assert.Equal(template.Length, built.Length);
			Assert.True(template.SequenceEqual(built), "from-scratch tails must equal BeachTailsTemplate.bin byte-for-byte");
		}

		// The captured-byte tests above only exercise the Strie baseline. The production flip (authoring the tails
		// instead of replaying the captured burst) relies on BuildFromScratch staying STRUCTURALLY valid for any
		// character - a different class/level patches different vitals into the stat block, and the snapshot framing
		// (the in-client hang risk) must survive that. This proves: the assembly re-parses, the three regions keep
		// their boundaries (quest portion = 1292, access-window catalog parses), the live vitals/XP land in the
		// right PostAccessBlock slots, and the round-trip is lossless - for a character that is NOT the capture.
		[Fact]
		public async Task BuildFromScratch_StaysValidForANonBaselineCharacter()
		{
			var def = await LoadDefinition(BeachStarterQuestId);
			Assert.NotNull(def);

			var character = new Character
			{
				Level = 7,
				Experience = 98760,
				MaxStats = new CharacterMaxStats
				{
					Health = 4321,
					Mana = 765,
					Stamina = 543
				}
			};
			const uint live = 0x0802ABCD;

			var built = FullCharSnapshotSubClass.BuildFromScratch(def, character, live);

			// Re-parses without throwing, and is byte-lossless through the typed model.
			var tails = FullCharSnapshotSubClass.Deserialize(built);
			Assert.True(built.SequenceEqual(tails.ToArray()), "non-baseline from-scratch tails must round-trip losslessly");

			// Region boundaries intact: the quest portion is the fixed beach length, the static access-window catalog parsed.
			Assert.Equal(FullCharSnapshotSubClass.QuestPortionLength, tails.KnownHeaderLength + QuestRegionTrailerLength(tails));
			Assert.NotEmpty(tails.AccessWindowEntries);

			// The live character's vitals/XP landed in the decoded stat slots (the part the production flip changes).
			Assert.NotNull(tails.PostAccessBlock);
			Assert.Equal(4321u, tails.PostAccessBlock!.CurrentHp);
			Assert.Equal(765u, tails.PostAccessBlock.CurrentMana);
			Assert.Equal(543u, tails.PostAccessBlock.CurrentStamina);
			Assert.Equal(98760u, tails.PostAccessBlock.CurrentXp);
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
			var options = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={DbPath}").Options;
			var repo = new DatabaseRepository(new Factory(options));
			return await repo.GetQuestDefinition(questId);
		}

		// The quest portion = the Quest_c journal (KnownHeaderLength) + the @609 objective region + the index region.
		private static int QuestRegionTrailerLength(FullCharSnapshotSubClass tails)
		{
			return (tails.QuestObjectives?.ToArray().Length ?? 0) + (tails.QuestIndex?.ToArray().Length ?? 0);
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
