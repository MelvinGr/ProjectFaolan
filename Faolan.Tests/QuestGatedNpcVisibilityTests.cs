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
		private const string Tuut = "Scavenger Tuut";
		private const uint TheScavenger = 13479; // Kill Tuut - the goal that carries GatesTargetVisibility
		private const uint TheScavengersKey = 13563; // Loot the key OFF Tuut - targets him, but is not the flagged goal

		[Fact]
		public async Task GatedNpc_IsRevealedByEveryQuestTargetingIt_NotOnlyTheFlaggedGoal()
		{
			var map = await Repo().GetQuestGatedNpcMap();

			Assert.True(map.TryGetValue(Tuut, out var reveals), $"{Tuut} is a quest-gated NPC");
			Assert.Contains(TheScavenger, reveals); // the flagged Kill goal still gates him
			Assert.Contains(TheScavengersKey, reveals); // ...and the Loot quest keeps him visible to hand the key over
		}

		[Fact]
		public async Task TheFlagStillDecidesWhoIsGatedAtAll()
		{
			// Widening the REVEAL set must not widen the GATED set: an NPC no goal flags stays ungated, so a
			// mechanically-resolved kill target (#521) does not hide its population or stop respawning. Every gated
			// name must own at least one flagged goal.
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;
			var map = await Repo().GetQuestGatedNpcMap();

			foreach (var name in map.Keys)
			{
				Assert.True(
					await db.QuestGoalDefinitions.AnyAsync(g => g.TargetName == name && g.GatesTargetVisibility, ct),
					$"{name} is gated, so some goal targeting it must carry GatesTargetVisibility");
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
