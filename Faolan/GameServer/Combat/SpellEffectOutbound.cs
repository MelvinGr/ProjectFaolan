using System;
using System.Threading.Tasks;
using Faolan.Core.Network;

namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     The three wire ops a <c>SpellEffect</c> row needs, so <see cref="SpellEffectService" /> and
	///     <see cref="FatalityService" /> dispatch data-driven effects without holding the listener. Each member is one
	///     forward to the service that owns the act: the buff registry and the pet lifecycle.
	/// </summary>
	/// <remarks>
	///     The construction order matters: <see cref="BuffService" /> must not depend on
	///     <see cref="CombatService" />, or the graph closes a cycle through this class ->
	///     <see cref="BuffService" /> -> <see cref="CombatService" /> -> <see cref="FatalityService" /> -> this class.
	///     The world-model reads that would otherwise pull it in belong on <c>WorldSimulation</c>.
	/// </remarks>
	public sealed class SpellEffectOutbound : ISpellEffectOutbound
	{
		private readonly BuffService _buffs;
		private readonly PetService _pets;

		public SpellEffectOutbound(BuffService buffs, PetService pets)
		{
			_buffs = buffs;
			_pets = pets;
		}

		public Task ApplySelfBuff(INetworkClient client, uint wireSpellId, bool walkChain = true)
		{
			var self = client.Session.Character.Id;
			return _buffs.ApplySpell(client, self, self, wireSpellId, walkChain
				? null
				: SpellChainWalk.Registry());
		}

		public async Task<bool> SummonPet(INetworkClient client, string template, bool combat, uint pointCost)
		{
			return await _pets.SummonPetForPlayer(client, template, combat, pointCost) != null;
		}

		public Task SummonTimedPet(INetworkClient client, string template, TimeSpan duration, TimeSpan? cap, uint pointCost)
		{
			return _pets.SummonTimedPetForPlayer(client, template, duration, cap, pointCost);
		}
	}
}
