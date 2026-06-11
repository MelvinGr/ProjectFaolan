namespace Faolan.GameServer.Scripting
{
	// The `player` API the RDB Lua scripts call (the surface observed across all 1992 LuaArchive scripts; see
	// protocol_conversation_routing). Conditions read state (HasTag/HasCompletedQuest/GetIntStat); effects mutate it
	// (AddTag, SpawnItem, SolveGoal, CastSpell). The runtime backs this with the character's tags + game systems; tests
	// use an in-memory implementation. Unknown/rare calls can no-op safely (the VM is permissive).
	public interface ILuaPlayer
	{
		void AddTag(int tagId);
		void CastSpell(int spellId);
		void Count(int counterId);
		int GetIntStat(int statId);
		string GetTagName(int tagId);
		bool HasBuff(int buffId);
		bool HasCompletedQuest(int questId);
		bool HasTag(int tagId);
		void SolveGoal(int goalId);
		void SpawnItem(int resourceHash, int count);
	}
}
