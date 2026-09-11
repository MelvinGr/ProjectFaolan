using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Repositories
{
	// What the character is on: its quest rows, goal progress and reward picks. One of the five character slices;
	// the row itself is ICharacterRepository.
	public interface ICharacterQuestRepository
	{
		/// <summary>
		///     A character's secondary-goal progress rows. <paramref name="questId" /> filters to one quest; null (the
		///     default) returns every held quest's rows in one query, which is what the world-entry journal wants - it
		///     needs the progress of every active quest's goals and would otherwise issue one call per quest.
		/// </summary>
		Task<CharacterQuestGoal[]> GetCharacterQuestGoals(uint characterId, uint? questId = null);
		Task<CharacterQuest[]> GetCharacterQuests(uint characterId, QuestState? state = null);
		Task RemoveCharacterQuest(uint characterId, uint questId);
		Task SetCharacterQuest(uint characterId, uint questId, QuestState state, uint goalProgress = 0, uint goalRequired = 0, string? goalTargetName = null);

		// Record the pick-one-of-N reward `characterId` took for `questId` - the idempotence guard for the grant.
		Task SetCharacterQuestReward(uint characterId, uint questId, uint chosenPos);
		Task SetCharacterQuestGoal(uint characterId, uint questId, uint goalId, uint progress);
	}

	public partial class DatabaseRepository
	{
		public async Task<CharacterQuestGoal[]> GetCharacterQuestGoals(uint characterId, uint? questId = null)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterQuestGoals
				.Where(g => g.CharacterId == characterId && (questId == null || g.QuestId == questId))
				.ToArrayAsync();
		}

		// `state` filters in SQL rather than in the caller: the goal-matching paths want only the active quests, and a
		// level-80 character holds 1500+ completed ones they would otherwise materialise and discard on every call.
		// Null (the default) returns every held quest, which is what the journal and the escort lookup want.
		public async Task<CharacterQuest[]> GetCharacterQuests(uint characterId, QuestState? state = null)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterQuests.Where(q => q.CharacterId == characterId && (state == null || q.State == state)).ToArrayAsync();
		}

		public async Task RemoveCharacterQuest(uint characterId, uint questId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.CharacterQuests.Where(q => q.CharacterId == characterId && q.QuestId == questId).ExecuteDeleteAsync();
		}

		// Record which pick-one-of-N reward the character took for a quest. Writing it is what makes the grant
		// idempotent - the selection is an ordinary c2s packet and can arrive any number of times.
		public async Task SetCharacterQuestReward(uint characterId, uint questId, uint chosenPos)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.CharacterQuests.Where(q => q.CharacterId == characterId && q.QuestId == questId)
				.ExecuteUpdateAsync(s => s.SetProperty(q => q.RewardChosenPos, chosenPos));
		}

		public async Task SetCharacterQuest(uint characterId, uint questId, QuestState state, uint goalProgress = 0, uint goalRequired = 0, string? goalTargetName = null)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var row = await ctx.CharacterQuests.FirstOrDefaultAsync(q => q.CharacterId == characterId && q.QuestId == questId);
			if (row == null)
			{
				ctx.CharacterQuests.Add(new CharacterQuest
				{
					CharacterId = characterId,
					QuestId = questId,
					State = state,
					GoalProgress = goalProgress,
					GoalRequired = goalRequired,
					GoalTargetName = goalTargetName
				});
			}
			else
			{
				row.State = state;
				row.GoalProgress = goalProgress;
				row.GoalRequired = goalRequired;
				row.GoalTargetName = goalTargetName;
			}

			await ctx.SaveChangesAsync();
		}

		public async Task SetCharacterQuestGoal(uint characterId, uint questId, uint goalId, uint progress)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var row = await ctx.CharacterQuestGoals.FirstOrDefaultAsync(g => g.CharacterId == characterId && g.QuestId == questId && g.GoalId == goalId);
			if (row == null)
			{
				ctx.CharacterQuestGoals.Add(new CharacterQuestGoal
				{
					CharacterId = characterId,
					QuestId = questId,
					GoalId = goalId,
					Progress = progress
				});
			}
			else
				row.Progress = progress;

			await ctx.SaveChangesAsync();
		}
	}
}
