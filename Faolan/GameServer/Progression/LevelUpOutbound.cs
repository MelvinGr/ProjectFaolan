using System;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.GameServer.Combat;

namespace Faolan.GameServer.Progression
{
	/// <summary>
	///     What a LEVEL-UP pushes to the client (#826), off GameServerListener: the re-seeded vitals plus the three
	///     "you gained this" messages. The senders themselves stay in the listener's .Outbound partial and are reached
	///     statically - they hold no state - so this owns the level-up SHAPE without owning the wire builders.
	/// </summary>
	public sealed class LevelUpOutbound : ILevelUpOutbound
	{
		// ⚠️ BOUND, not injected, and this is a real CYCLE rather than a preference: CombatService takes
		// LevelUpService, which takes this. Passing `_combat` in the ctor captured a field that is not assigned until
		// 16 lines later - it compiled clean and held NULL, so the first level-up would have thrown. Same shape and
		// same fix as EconomyOutbound's collect-goal hook.
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
			return GameServerListener.SendCombatRatings(client);
		}
		public Task AnnounceAbility(INetworkClient client, Ability ability)
		{
			return GameServerListener.SendAddSpecialAbility(client, ability);
		}
		public Task LearnSpell(INetworkClient client, uint spellId)
		{
			return GameServerListener.SendLearnSpell(client, spellId);
		}
	}
}
