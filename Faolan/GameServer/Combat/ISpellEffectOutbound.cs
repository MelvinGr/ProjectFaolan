using System;
using System.Threading.Tasks;
using Faolan.Core.Network;

namespace Faolan.GameServer.Combat
{
	// The wire side of a SpellEffect row: the operations running one needs from the listener. Implemented by the
	// GameServer listener (which owns the packet framing, the pet spawn + PetIIR assign and the buff registry), so
	// SpellEffectService depends on this narrow port rather than on the whole listener.
	public interface ISpellEffectOutbound
	{
		// Apply a spell to the player as a self-buff (ApplySpell, caster == subject). Takes the WIRE id
		// ((base << 8) | rank), the same form every ApplySpell send path uses.
		//
		// `walkChain: false` applies the spell ALONE, leaving its 53051 rows unwalked - for a carrier whose rows are a
		// candidate REGISTRY rather than #784's "when this lands, also run X". See FatalityService.
		Task ApplySelfBuff(INetworkClient client, uint wireSpellId, bool walkChain = true);

		// Summon a creature TYPE as the player's pet. Returns false when no spawnable type matches the name - a
		// content gap the caller reports rather than swallows.
		//
		// `pointCost` is the summoning spell's own `SummonPointCost`, and it goes ON THE WIRE: the PetIIR assign's
		// `f2` slot is what the client's pet-point bar counts (#823), and the server is the only thing that ever
		// states it (stat 0x1C8 is never sent).
		Task<bool> SummonPet(INetworkClient client, string template, bool combat, uint pointCost);

		// Summon a pet that lives for `duration`, or EXTEND the one already out (clamped to `cap`, if any).
		Task SummonTimedPet(INetworkClient client, string template, TimeSpan duration, TimeSpan? cap, uint pointCost);
	}
}
