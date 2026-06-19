using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.GameServer.Packets.FullCharSnapshot;
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
		private const string TemplatePath = "/home/melvin/Development/C#/Project Faolan/Faolan/GameServer/Packets/FullCharSnapshot/BeachTailsTemplate.bin";
		private const uint BeachStarterQuestId = 10238;
		private const uint TemplateInstance = 0x02165EFF;

		[Fact]
		public async Task BuildFromScratch_EmbedsTheEmptyQuestPortion_NonQuestTailUnchanged()
		{
			var def = await LoadDefinition(BeachStarterQuestId);
			Assert.NotNull(def);

			// #254: the journal is no longer baked into the snapshot - the quest portion is the EMPTY form and the
			// journal is delivered live (RestorePlayerQuests). character = null -> the stat skeleton stays at its
			// (Strie) baseline, instance = the template's. So the tails = [empty quest portion][the template's
			// non-quest tail (access-window catalog + stat block), byte-for-byte unchanged].
			var built = BeachTailsBuilder.BuildFromScratch(def, character: null, TemplateInstance);
			var empty = BeachTailsBuilder.BuildEmptyQuestPortion();
			var template = File.ReadAllBytes(TemplatePath);

			Assert.True(built.AsSpan(0, empty.Length).SequenceEqual(empty), "snapshot quest portion must be the empty form (no embedded journal)");
			Assert.True(built.AsSpan(empty.Length).SequenceEqual(template.AsSpan(BeachTailsBuilder.QuestPortionLength)),
				"the non-quest tail (access-window catalog + stat block) must stay byte-for-byte the template's");
		}

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
			var built = BeachTailsBuilder.BuildFromScratch(def, strie, TemplateInstance);

			// The non-quest tail (the authored per-class stat block + access-window) must still equal the captured
			// Strie template byte-for-byte for the baseline Assassin (the quest portion is the empty form now, #254).
			var empty = BeachTailsBuilder.BuildEmptyQuestPortion();
			var template = File.ReadAllBytes(TemplatePath);
			Assert.True(built.AsSpan(empty.Length).SequenceEqual(template.AsSpan(BeachTailsBuilder.QuestPortionLength)),
				"the authored non-quest tail for an Assassin L1 must equal the captured Strie template byte-for-byte");
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
				MaxStats = new CharacterMaxStats { Health = 4321, Mana = 765, Stamina = 543 }
			};
			const uint live = 0x0802ABCD;

			var built = BeachTailsBuilder.BuildFromScratch(def, character, live);

			// Re-parses without throwing, and is byte-lossless through the typed model.
			var tails = SnapshotTails.Deserialize(built);
			Assert.True(built.SequenceEqual(tails.ToArray()), "non-baseline from-scratch tails must round-trip losslessly");

			// Region boundaries intact: the quest portion is now the EMPTY form (#254), the static access-window catalog parsed.
			Assert.Equal(BeachTailsBuilder.BuildEmptyQuestPortion().Length, tails.KnownHeaderLength + QuestRegionTrailerLength(tails));
			Assert.NotEmpty(tails.AccessWindowEntries);

			// The live character's vitals/XP landed in the decoded stat slots (the part the production flip changes).
			Assert.NotNull(tails.PostAccessBlock);
			Assert.Equal(4321u, tails.PostAccessBlock!.CurrentHp);
			Assert.Equal(765u, tails.PostAccessBlock.CurrentMana);
			Assert.Equal(543u, tails.PostAccessBlock.CurrentStamina);
			Assert.Equal(98760u, tails.PostAccessBlock.CurrentXp);
		}

		// The quest portion = the Quest_c journal (KnownHeaderLength) + the @609 objective region + the index region.
		private static int QuestRegionTrailerLength(SnapshotTails tails) =>
			(tails.QuestObjectives?.ToArray().Length ?? 0) + (tails.QuestIndex?.ToArray().Length ?? 0);

		[Fact]
		public async Task BuildFromScratch_CarriesNoPerPlayerInstance_WithEmptyQuest()
		{
			var def = await LoadDefinition(BeachStarterQuestId);
			const uint live = 0x0802ABCD;
			var built = BeachTailsBuilder.BuildFromScratch(def, character: null, live);

			// #254: the ONLY per-player instance ref in the tails was the quest objective region - now the empty form,
			// with the journal (and its live instance) delivered via RestorePlayerQuests. So the tails carry neither
			// the template instance nor the live one; the snapshot is addressed via the RPC header, not a raw tail ref.
			Assert.False(Contains(built, new byte[] { 0x02, 0x16, 0x5E, 0xFF }), "template instance must not appear (was quest-objective only)");
		}

		private static bool Contains(byte[] hay, byte[] needle)
		{
			for (var i = 0; i + needle.Length <= hay.Length; i++)
				if (hay.AsSpan(i, needle.Length).SequenceEqual(needle))
					return true;
			return false;
		}

		private static async Task<QuestDefinition> LoadDefinition(uint questId)
		{
			var options = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={DbPath}").Options;
			var repo = new DatabaseRepository(new Factory(options));
			return await repo.GetQuestDefinition(questId);
		}

		private sealed class Factory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
		{
			public DatabaseContext CreateDbContext() => new(options);
		}
	}
}
