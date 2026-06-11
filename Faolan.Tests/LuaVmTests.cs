using System.Collections.Generic;
using Faolan.GameServer.Scripting;
using Xunit;

namespace Faolan.Tests
{
	// The RDB Lua VM (protocol_conversation_routing): run real LuaArchive scripts against a player's tags.
	public class LuaVmTests
	{
		private readonly LuaScriptVm _vm = new LuaScriptVm();

		[Fact]
		public void Condition_BadScript_DefaultsPermissive()
		{
			// A script that references an undefined global must not hard-block (returns the permissive default).
			Assert.True(_vm.RunCondition("return somethingUndefined:Nope()", new FakePlayer()));
			Assert.False(_vm.RunCondition("return somethingUndefined:Nope()", new FakePlayer(), false));
		}

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
		public void Effect_SpawnItem_ResolvesResourceHash()
		{
			var p = new FakePlayer();
			Assert.True(_vm.RunEffect("player:SpawnItem(Resource.Hash(\"K3CF\"),1)", p));
			Assert.Single(p.SpawnedItems);
			Assert.Equal(1, p.SpawnedItems[0].Count);
			Assert.NotEqual(0, p.SpawnedItems[0].Hash); // hashed from "K3CF"
		}

		// In-memory player: tags are a set; effects are recorded so tests can assert them.
		private sealed class FakePlayer : ILuaPlayer
		{
			public readonly HashSet<int> Tags = new HashSet<int>();
			public readonly List<(int Hash, int Count)> SpawnedItems = new List<(int Hash, int Count)>();
			public readonly List<int> CastSpells = new List<int>();
			public readonly List<int> SolvedGoals = new List<int>();

			public void AddTag(int tagId)
			{
				Tags.Add(tagId);
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
				return false;
			}

			public bool HasTag(int tagId)
			{
				return Tags.Contains(tagId);
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
}
