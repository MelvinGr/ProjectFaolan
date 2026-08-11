using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// #574: a quest-gated NPC must stay visible while ANY quest still needs it, not only while the ONE goal flagged
	// `GatesTargetVisibility` is active.
	//
	// The Casilda chain softlocked on exactly that. Scavenger Tuut is revealed by 13479 "The Scavenger" (the flagged
	// Kill goal), but 13563 "The Scavenger's Key" (Loot) targets him too - and killing him COMPLETES 13479, the only
	// quest that was revealing him. He is `NoRespawn` besides, a story kill target that stays dead. So once his loot
	// bag was gone - expiry, or a server restart, since bags are live world state and are not persisted - Tuut was
	// invisible for good, the key unobtainable and Casilda unfreeable. Reported in-client: "killed tuut but did not
	// loot ... she is still locked".
	public class QuestGatedNpcVisibilityTests
	{
		private const uint Tuut = 11561; // the Scavenger Tuut NPC TEMPLATE (#521: the map is keyed by template, not name)
		private const uint TheScavenger = 13479; // Kill Tuut - the goal that carries GatesTargetVisibility
		private const uint TheScavengersKey = 13563; // Loot the key OFF Tuut - targets him, but is not the flagged goal

		// The GALLEY Saddur - a second template sharing the beach Saddur's display name, on the Stygian Galley (4000)
		// rather than Tortage Island (4010). No quest targets it and nothing flags it, so it must not be gated.
		private const uint GalleySaddur = 2993;

		[Fact]
		public async Task GatedNpc_IsRevealedByEveryQuestTargetingIt_NotOnlyTheFlaggedGoal()
		{
			var map = await Repo().GetQuestGatedNpcMap();

			Assert.True(map.TryGetValue(Tuut, out var reveals), "Scavenger Tuut is a quest-gated NPC");
			Assert.Contains(TheScavenger, reveals); // the flagged Kill goal still gates him
			Assert.Contains(TheScavengersKey, reveals); // ...and the Loot quest keeps him visible to hand the key over
		}

		// #521: the gate used to key on the NAME, which stamped GateQuestIds + NoRespawn onto EVERY same-named template.
		// `Saddur` is two of them and goal 3490 flags only the Tortage Island one (9500), so the Stygian Galley Saddur
		// was hidden from every player not carrying a Tortage Island quest they cannot have yet - on the intro playfield.
		[Fact]
		public async Task ASameNamedNpcThatNoQuestTargets_IsNotGated()
		{
			var map = await Repo().GetQuestGatedNpcMap();

			Assert.True(map.ContainsKey(9500), "the Tortage Island Saddur IS the flagged kill target");
			Assert.False(map.ContainsKey(GalleySaddur), "the Stygian Galley Saddur merely shares his name and must stay visible");
		}

		[Fact]
		public async Task TheFlagStillDecidesWhoIsGatedAtAll()
		{
			// Widening the REVEAL set must not widen the GATED set: an NPC no goal flags stays ungated, so a
			// mechanically-resolved kill target (#521) does not hide its population or stop respawning. Every gated
			// template must own at least one flagged goal.
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;
			var map = await Repo().GetQuestGatedNpcMap();

			foreach (var templateId in map.Keys)
			{
				Assert.True(
					await db.QuestGoalDefinitions.AnyAsync(g => g.TargetTemplateId == templateId && g.GatesTargetVisibility, ct),
					$"template {templateId} is gated, so some goal targeting it must carry GatesTargetVisibility");
			}
		}

		// #708: staying dead is a SEPARATE query off a separate column. Both story targets want both today, which is
		// what let one column answer both questions - the split is what makes the first quest-only target that must
		// RESPAWN (a repeatable or retryable quest) expressible at all.
		[Fact]
		public async Task StayingDead_IsItsOwnFlag_NotImpliedByTheGate()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;
			var stayDead = await Repo().GetStoryKillTargets();

			Assert.Contains(Tuut, stayDead); // the story kill targets carry both flags
			Assert.Contains(9500u, stayDead);
			Assert.DoesNotContain(GalleySaddur, stayDead);

			// The two columns are read independently: nothing derives one from the other, so a row could carry either
			// alone. (No shipped row does today - that is data, not a rule, and this is what keeps it that way.)
			foreach (var templateId in stayDead)
			{
				Assert.True(
					await db.QuestGoalDefinitions.AnyAsync(g => g.TargetTemplateId == templateId && g.TargetStaysDead, ct),
					$"template {templateId} stays dead, so some goal targeting it must carry TargetStaysDead");
			}
		}

		private static DatabaseRepository Repo()
		{
			return new DatabaseRepository(new Factory(TestDb.ReadOnly(TestDb.GameDatabase)));
		}

		private sealed class Factory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
		{
			public DatabaseContext CreateDbContext()
			{
				return new DatabaseContext(options);
			}

			public Task<DatabaseContext> CreateDbContextAsync(CancellationToken ct = default)
			{
				return Task.FromResult(new DatabaseContext(options));
			}
		}
	}
}
