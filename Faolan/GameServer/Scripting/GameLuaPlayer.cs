using System.Collections.Generic;

namespace Faolan.GameServer.Scripting
{
	// Runtime ILuaPlayer for a real character: HasTag/HasCompletedQuest read preloaded state (script calls are sync,
	// so state is loaded up front); AddTag/SpawnItem/SolveGoal/CastSpell are RECORDED here and applied by the caller
	// after the script runs (persist new tags, advance goals, etc.). Built per script run.
	public sealed class GameLuaPlayer : ILuaPlayer
	{
		private readonly HashSet<int> _tags;
		private readonly HashSet<int> _completedQuests;

		public GameLuaPlayer(IEnumerable<uint> tags, IEnumerable<uint> completedQuests)
		{
			_tags = new HashSet<int>();
			foreach (var t in tags)
				_tags.Add((int)t);
			_completedQuests = new HashSet<int>();
			foreach (var q in completedQuests)
				_completedQuests.Add((int)q);
		}

		// Effects recorded during the run, for the caller to persist/apply.
		public List<int> AddedTags { get; } = new();
		public List<int> SolvedGoals { get; } = new();
		public List<(int Hash, int Count)> SpawnedItems { get; } = new();
		public List<int> CastSpells { get; } = new();

		public bool HasTag(int tagId) => _tags.Contains(tagId);

		public void AddTag(int tagId)
		{
			if (_tags.Add(tagId))
				AddedTags.Add(tagId);
		}

		public bool HasCompletedQuest(int questId) => _completedQuests.Contains(questId);
		public int GetIntStat(int statId) => 0;
		public void Count(int counterId) { }
		public void SpawnItem(int resourceHash, int count) => SpawnedItems.Add((resourceHash, count));
		public void SolveGoal(int goalId) => SolvedGoals.Add(goalId);
		public void CastSpell(int spellId) => CastSpells.Add(spellId);
		public string GetTagName(int tagId) => $"Tag{tagId}";
		public bool HasBuff(int buffId) => false;
	}
}
