using System;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.GameServer.Combat;
using Faolan.GameServer.Wire;

namespace Faolan.GameServer.Progression
{
	/// <summary>
	///     What a level-up pushes to the client: the re-seeded vitals plus the three "you gained this" messages. The
	///     senders themselves are static and hold no state, so this owns the level-up shape without owning the wire
	///     builders.
	/// </summary>
	public sealed class LevelUpOutbound : ILevelUpOutbound
	{
		// Bound rather than injected, because this is a genuine cycle: CombatService takes LevelUpService, which takes
		// this. Passing `_combat` in the constructor would capture a field that is not assigned until later - it
		// compiles clean and holds null, so the first level-up would throw. Same shape as EconomyOutbound's
		// collect-goal hook.
		private Func<INetworkClient, Task<EntityVitals>> _reseedFromMaxStats = null!;

		// Bound in the listener ctor immediately after CombatService exists, long before any XP can be awarded.
		internal void BindReseed(Func<INetworkClient, Task<EntityVitals>> reseedFromMaxStats)
		{
			_reseedFromMaxStats = reseedFromMaxStats;
		}

		// --- ILevelUpOutbound: the wire side of a ding. Explicit forwards to the ops that already existed here, which
		// is what keeps LevelUpService off the listener itself - see Progression/ILevelUpOutbound.cs.
		public Task<EntityVitals> ReseedVitals(INetworkClient client)
		{
			return _reseedFromMaxStats(client);
		}
		public Task SendCombatRatings(INetworkClient client)
		{
			return GameWire.SendCombatRatings(client);
		}
		public Task AnnounceAbility(INetworkClient client, Ability ability)
		{
			return GameWire.SendAddSpecialAbility(client, ability);
		}
		public Task LearnSpell(INetworkClient client, uint spellId)
		{
			return GameWire.SendLearnSpell(client, spellId);
		}
	}
}
