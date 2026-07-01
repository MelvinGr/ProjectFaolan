using System;
using MoonSharp.Interpreter;

namespace Faolan.GameServer.Scripting
{
	// Runs the RDB LuaArchive scripts (protocol_conversation_routing) against a player context, using MoonSharp (a
	// pure-C# sandboxed Lua interpreter). Scripts are tiny: a CONDITION returns true/false (gates a dialogue option /
	// quest), an EFFECT mutates state (player:AddTag/SpawnItem/SolveGoal/CastSpell). This is the engine the
	// conversation/quest VM is built on. Stateless + reusable; the per-player state lives behind ILuaPlayer.
	public sealed class LuaScriptVm
	{
		static LuaScriptVm()
		{
			UserData.RegisterType<LuaPlayerProxy>();
		}

		// Evaluates a CONDITION script (returns true/false). On error or a non-boolean result, defaults to `permissive`
		// so a missing/odd condition never hard-blocks a dialogue option.
		public bool RunCondition(string source, ILuaPlayer player, bool permissive = true)
		{
			try
			{
				var result = Build(player).DoString(source);
				return result.Type == DataType.Boolean
					? result.Boolean
					: permissive;
			}
			catch (InterpreterException)
			{
				return permissive;
			}
		}

		// Runs an EFFECT script (AddTag/SpawnItem/...). Returns false (swallowed) on a script error so one bad script
		// doesn't break the caller; true if it ran clean.
		public bool RunEffect(string source, ILuaPlayer player)
		{
			try
			{
				Build(player).DoString(source);
				return true;
			}
			catch (InterpreterException)
			{
				return false;
			}
		}

		private static Script Build(ILuaPlayer player)
		{
			var script = new Script(CoreModules.Preset_HardSandbox); // no IO/os/loadfile - safe to run untrusted RDB Lua

			var proxy = UserData.Create(new LuaPlayerProxy(player));
			script.Globals["player"] = proxy;
			script.Globals["owner"] = proxy; // aliases seen in a few scripts
			script.Globals["character"] = proxy;

			var resource = new Table(script);
			resource["Hash"] = (Func<string, double>)HashResource; // Resource.Hash("ABCD") - dotted, no self
			resource["SpellID"] = (Func<DynValue, double>)(_ => 0);
			script.Globals["Resource"] = resource;

			return script;
		}

		// Resource.Hash("ABCD") - the client's 4-char resource code → an id. We don't have the resource registry, so
		// this is a stable hash of the code (enough to key SpawnItem logging / future resolution).
		private static double HashResource(string code)
		{
			uint h = 0;
			foreach (var c in code ?? "")
				h = h * 31 + c;
			return h;
		}
	}

	// CLR object MoonSharp binds `player:Method(args)` to (it handles self + Lua-number→int coercion). Delegates to
	// the game-side ILuaPlayer. Exposes the full observed API surface; rare calls are no-ops so a script using them
	// doesn't fault.
	[MoonSharpUserData]
	public sealed class LuaPlayerProxy
	{
		private readonly ILuaPlayer _p;

		public LuaPlayerProxy(ILuaPlayer p)
		{
			_p = p;
		}

		public void AddTag(int tagId)
		{
			_p.AddTag(tagId);
		}

		public void castspell(int spellId)
		{
			_p.CastSpell(spellId);
			// lowercase variant in some scripts
		}

		public void CastSpell(int spellId)
		{
			_p.CastSpell(spellId);
		}

		public void Count(int counterId)
		{
			_p.Count(counterId);
		}

		public int GetIntStat(int statId)
		{
			return _p.GetIntStat(statId);
		}

		public string GetTagName(int tagId)
		{
			return _p.GetTagName(tagId);
		}

		public bool HasBuff(int buffId)
		{
			return _p.HasBuff(buffId);
		}

		public bool HasCompletedQuest(int questId)
		{
			return _p.HasCompletedQuest(questId);
		}

		public bool HasQuest(int questId)
		{
			return _p.HasQuest(questId);
		}

		public bool HasTag(int tagId)
		{
			return _p.HasTag(tagId);
		}

		public void OpenSiteInBrowser(string url)
		{
		}

		public void SolveGoal(int goalId)
		{
			_p.SolveGoal(goalId);
		}

		public void SpawnItem(int resourceHash, int count)
		{
			_p.SpawnItem(resourceHash, count);
		}

		public void TriggerGetEffect(int effectId)
		{
		}
	}
}
