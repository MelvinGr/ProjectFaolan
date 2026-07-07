using Faolan.GameServer.Scripting;
using Xunit;

namespace Faolan.Tests
{
	// The condition-gated dialogue VM building blocks (issue #37): a ConversationNode.ConditionScript is a Lua
	// snippet run against the live player (tags + completed quests) by LuaScriptVm.RunCondition; the runtime serves
	// the variant whose condition passes (unconditional = fallback). This locks the quest-gate evaluation that picks
	// Casilda's pre- vs post-first-quest dialogue.
	public class ConversationConditionTests
	{
		[Fact]
		public void BadCondition_DoesNotMatch_WhenNonPermissive()
		{
			var vm = new LuaScriptVm();
			// A malformed gate must not hijack every player (the runtime uses permissive: false).
			Assert.False(LuaScriptVm.RunCondition("this is not lua", new GameLuaPlayer([], []), false));
		}

		[Fact]
		public void HasCompletedQuest_GatesTheVariant()
		{
			var vm = new LuaScriptVm();
			const string condition = "return player:HasCompletedQuest(13479)";

			var afterQuest = new GameLuaPlayer([], [13479u]);
			var beforeQuest = new GameLuaPlayer([], []);

			Assert.True(LuaScriptVm.RunCondition(condition, afterQuest, false));
			Assert.False(LuaScriptVm.RunCondition(condition, beforeQuest, false));
		}

		[Fact]
		public void HasTag_GatesTheVariant()
		{
			var vm = new LuaScriptVm();
			const string condition = "return player:HasTag(5001)";

			Assert.True(LuaScriptVm.RunCondition(condition, new GameLuaPlayer([5001u], []), false));
			Assert.False(LuaScriptVm.RunCondition(condition, new GameLuaPlayer([], []), false));
		}
	}
}
