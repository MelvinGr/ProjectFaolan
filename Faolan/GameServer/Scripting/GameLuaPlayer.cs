using System.Collections.Generic;

namespace Faolan.GameServer.Scripting
{
	// Runtime ILuaPlayer for a real character: HasTag/HasCompletedQuest read preloaded state (script calls are sync,
	// so state is loaded up front); AddTag/SpawnItem/SolveGoal/CastSpell are RECORDED here and applied by the caller
	// after the script runs (persist new tags, advance goals, etc.). Built per script run.
	public sealed class GameLuaPlayer : ILuaPlayer
	{
		private readonly HashSet<int> _activeQuests;
		private readonly HashSet<int> _completedQuests;
		private readonly HashSet<int> _tags;
		public List<(int Hash, int Count)> SpawnedItems { get; } = [];

		// Effects recorded during the run, for the caller to persist/apply.
		public List<int> AddedTags { get; } = [];
		public List<int> CastSpells { get; } = [];
		public List<int> SolvedGoals { get; } = [];

		public GameLuaPlayer(IEnumerable<uint> tags, IEnumerable<uint> completedQuests, IEnumerable<uint> activeQuests = null)
		{
			_tags = [];
			foreach (var t in tags)
				_tags.Add((int)t);
			_completedQuests = [];
			foreach (var q in completedQuests)
				_completedQuests.Add((int)q);
			_activeQuests = [];
			if (activeQuests != null)
			{
				foreach (var q in activeQuests)
					_activeQuests.Add((int)q);
			}
		}

		public void AddTag(int tagId)
		{
			if (_tags.Add(tagId))
				AddedTags.Add(tagId);
		}

		public void CastSpell(int spellId)
		{
			CastSpells.Add(spellId);
		}

		public void Count(int counterId)
		{
		}

		public int GetIntStat(int statId)
		{
			return 0;
		}

		public string GetTagName(int tagId)
		{
			return $"Tag{tagId}";
		}

		public bool HasBuff(int buffId)
		{
			return false;
		}

		public bool HasCompletedQuest(int questId)
		{
			return _completedQuests.Contains(questId);
		}

		// "Has accepted it": active or already completed.
		public bool HasQuest(int questId)
		{
			return _activeQuests.Contains(questId) || _completedQuests.Contains(questId);
		}

		public bool HasTag(int tagId)
		{
			return _tags.Contains(tagId);
		}

		public void SolveGoal(int goalId)
		{
			SolvedGoals.Add(goalId);
		}

		public void SpawnItem(int resourceHash, int count)
		{
			SpawnedItems.Add((resourceHash, count));
		}
	}
}
