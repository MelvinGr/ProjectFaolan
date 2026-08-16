using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// The scry events an NPC's dialogue fires (#839) and, more to the point, the ACTION each is mapped to.
	//
	// The event STRING is retail's and is harvested; the action is ours, and it is the seam that lets a future quest's
	// script be plugged in by stamping a column instead of adding a branch to the dialogue runtime. These gates are
	// about that seam staying honest: the three we act on stay mapped, and everything else stays visibly UNmapped
	// rather than drifting into a behaviour nobody chose.
	public class NpcScriptEventTests
	{
		// The mapping the runtime dispatches on. Named here rather than counted, because a wrong action is a creature
		// doing the wrong thing in the world - Casilda despawning instead of spawning Tuut would look like a bug in
		// the escort, three subsystems away from the row that caused it.
		[Theory]
		[InlineData(9500u, "Fight", NpcScriptAction.TurnHostile)] // Saddur turns on the player
		[InlineData(9411u, "RunToTortage", NpcScriptAction.LeaveWorld)] // Casilda leaves for the city
		[InlineData(9411u, "SpawnScavenger", NpcScriptAction.RevealQuestTargets)] // ...and Tuut enters the world
		public async Task TheActedOnScriptEvents_KeepTheirAction(uint template, string name, NpcScriptAction expected)
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var row = await db.NpcScriptEvents.AsNoTracking().FirstOrDefaultAsync(e => e.NpcTemplateId == template && e.Event == name, ct);
			Assert.NotNull(row);
			Assert.Equal(expected, row!.Action);
		}

		// ⚠️ The other half, and the one that actually protects the seam: an event nobody has mapped must stay `None`.
		//
		// `None` is load-bearing exactly as `QuestGoalType.None` is - it means "harvested, stored, and logged when it
		// fires", not "do the nearest thing". `SpawnSheep` is the case that makes it concrete: it LOOKS like
		// `SpawnScavenger` and is not, because RevealQuestTargets reveals what a QUEST asks for and Ulfheoinn grants
		// no quest we hold and no goal gates a Sheep. Mapping it by name-similarity would fire a reveal that reveals
		// nothing and read as wired.
		[Fact]
		public async Task AnUnmappedScriptEvent_StaysNone_RatherThanGuessingAnAction()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var unmapped = await db.NpcScriptEvents.AsNoTracking().Where(e => e.Action == NpcScriptAction.None).Select(e => e.Event).ToListAsync(ct);
			Assert.Contains("SpawnSheep", unmapped);

			// Every mapped row must be one we can actually carry out - an action pointing at behaviour the runtime does
			// not implement is worse than None, because it reads as wired.
			var mapped = await db.NpcScriptEvents.AsNoTracking().Where(e => e.Action != NpcScriptAction.None).ToListAsync(ct);
			Assert.All(mapped, e => Assert.Contains(e.Action,
				new[] { NpcScriptAction.TurnHostile, NpcScriptAction.LeaveWorld, NpcScriptAction.RevealQuestTargets }));
		}

		// `RevealQuestTargets` says WHEN, and the quest says WHO - so an NPC that fires it must have a quest with a
		// gated target to reveal, or the event is a no-op that looks wired. Casilda grants 13479, whose goal 5744
		// gates Scavenger Tuut; that chain is what the action actually depends on.
		[Fact]
		public async Task ARevealScriptEvent_HasAGatedQuestTargetToReveal()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var revealers = await db.NpcScriptEvents.AsNoTracking().Where(e => e.Action == NpcScriptAction.RevealQuestTargets).Select(e => e.NpcTemplateId).ToListAsync(ct);
			Assert.NotEmpty(revealers);

			var gated = await db.QuestGoalDefinitions.AsNoTracking().Where(g => g.GatesTargetVisibility).ToListAsync(ct);
			foreach (var npc in revealers)
			{
				// The grant rides the EDGE the player takes, so the NPC is reached through the edge's From node.
				var grants = await db.ConversationEdges.AsNoTracking()
					.Where(e => e.GrantQuestId != null && e.From.NpcTemplateId == npc)
					.Select(e => e.GrantQuestId!.Value)
					.ToListAsync(ct);
				Assert.True(gated.Any(g => grants.Contains(g.QuestDefinitionId)),
					$"template {npc} fires a reveal but grants no quest with a visibility-gated target - the event would reveal nothing");
			}
		}
	}
}
