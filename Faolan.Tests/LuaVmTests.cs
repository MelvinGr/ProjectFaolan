using System.Collections.Generic;
using Faolan.GameServer.Scripting;
using Xunit;

namespace Faolan.Tests
{
	// The RDB Lua VM (protocol_conversation_routing): run real LuaArchive scripts against a player's tags.
	public class LuaVmTests
	{
		// In-memory player: tags are a set; effects are recorded so tests can assert them.
		private sealed class FakePlayer : ILuaPlayer
		{
			public readonly HashSet<int> Tags = new();
			public readonly List<int> SolvedGoals = new();
			public readonly List<(int Hash, int Count)> SpawnedItems = new();
			public readonly List<int> CastSpells = new();

			public bool HasTag(int tagId) => Tags.Contains(tagId);
			public void AddTag(int tagId) => Tags.Add(tagId);
			public bool HasCompletedQuest(int questId) => false;
			public int GetIntStat(int statId) => 0;
			public void Count(int counterId) { }
			public void SpawnItem(int resourceHash, int count) => SpawnedItems.Add((resourceHash, count));
			public void SolveGoal(int goalId) => SolvedGoals.Add(goalId);
			public void CastSpell(int spellId) => CastSpells.Add(spellId);
			public string GetTagName(int tagId) => $"Tag{tagId}";
			public bool HasBuff(int buffId) => false;
		}

		private readonly LuaScriptVm _vm = new();

		[Fact]
		public void Condition_HasTag_GatesOnRealScript()
		{
			// The actual LuaArchive condition shape.
			const string src = "if player:HasTag(3104) then\n\treturn false\nelse\n\treturn true\nend";
			var p = new FakePlayer();

			Assert.True(_vm.RunCondition(src, p)); // no tag -> true
			p.Tags.Add(3104);
			Assert.False(_vm.RunCondition(src, p)); // tag set -> false
		}

		[Fact]
		public void Effect_AddTag_MutatesPlayer()
		{
			var p = new FakePlayer();
			Assert.True(_vm.RunEffect("player:AddTag(2742)", p));
			Assert.Contains(2742, p.Tags);
		}

		[Fact]
		public void Effect_SpawnItem_ResolvesResourceHash()
		{
			var p = new FakePlayer();
			Assert.True(_vm.RunEffect("player:SpawnItem(Resource.Hash(\"K3CF\"),1)", p));
			Assert.Single(p.SpawnedItems);
			Assert.Equal(1, p.SpawnedItems[0].Count);
			Assert.NotEqual(0, p.SpawnedItems[0].Hash); // hashed from "K3CF"
		}

		[Fact]
		public void Effect_MultiStatement_RunsAll()
		{
			// A real multi-statement script form (AddTag + a guarded Count).
			const string src = "player:AddTag(1496)\nif not(player:HasTag(3104)) then\n\tplayer:Count(1000073)\nend";
			var p = new FakePlayer();
			Assert.True(_vm.RunEffect(src, p));
			Assert.Contains(1496, p.Tags);
		}

		[Fact]
		public void Effect_SolveGoal_Recorded()
		{
			var p = new FakePlayer();
			Assert.True(_vm.RunEffect("player:SolveGoal(5744)", p));
			Assert.Contains(5744, p.SolvedGoals);
		}

		[Fact]
		public void Condition_BadScript_DefaultsPermissive()
		{
			// A script that references an undefined global must not hard-block (returns the permissive default).
			Assert.True(_vm.RunCondition("return somethingUndefined:Nope()", new FakePlayer()));
			Assert.False(_vm.RunCondition("return somethingUndefined:Nope()", new FakePlayer(), permissive: false));
		}
	}
}
