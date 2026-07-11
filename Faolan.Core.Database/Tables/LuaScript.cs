using System.ComponentModel.DataAnnotations;

namespace Faolan.Core.Database.Tables
{
	// One Lua script from the client RDB LuaArchive (type 1010223) - the logic/branching behind conversations,
	// quests, and scripted objects (docs/rdb/rdb-extraction-plan.md, protocol_conversation_routing). Scripts are tiny and
	// call a `player` API: HasTag(n) (condition), AddTag(n) (effect), SpawnItem/SolveGoal/CastSpell/GetIntStat/Count.
	// Referenced by id from conversation/quest/object remoteformat scripts. Id = the RDB script id (not autoincrement).
	public class LuaScript : BaseEntity
	{
		// The Lua source text, e.g. "if player:HasTag(3104) then return false else return true end" or
		// "player:AddTag(2742)".
		[MaxLength(65536)]
		public string Source { get; set; } = null!;
	}
}
