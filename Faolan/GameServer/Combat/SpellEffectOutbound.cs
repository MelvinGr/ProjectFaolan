using System;
using System.Threading.Tasks;
using Faolan.Core.Network;

namespace Faolan.GameServer.Combat
{
	// ISpellEffectOutbound (#439) - the three wire ops a SpellEffect row needs, so SpellEffectService and
	// FatalityService dispatch data-driven effects without holding the listener. All three are one forward each, to
	// the service that owns the act: the buff registry and the pet lifecycle.
	//
	// Building it needed one edge cut first. BuffService took CombatService for a single call
	// (GetOrAddPlayerVitals), which closed a construction cycle - this class -> BuffService -> CombatService ->
	// FatalityService -> this class - and that cycle is the whole reason the listener implemented the port for so
	// long. The call reads nothing but the world model, so it moved to WorldSimulation and the cycle went with it.
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
