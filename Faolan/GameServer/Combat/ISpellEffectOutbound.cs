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
		Task ApplySelfBuff(INetworkClient client, uint wireSpellId);

		// Summon a creature TYPE as the player's pet. Returns false when no spawnable type matches the name - a
		// content gap the caller reports rather than swallows.
		Task<bool> SummonPet(INetworkClient client, string template, bool combat);

		// Summon a pet that lives for `duration`, or EXTEND the one already out (clamped to `cap`, if any).
		Task SummonTimedPet(INetworkClient client, string template, TimeSpan duration, TimeSpan? cap);
	}
}
