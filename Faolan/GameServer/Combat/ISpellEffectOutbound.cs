using System;
using System.Threading.Tasks;
using Faolan.Core.Network;

namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     The wire side of a <c>SpellEffect</c> row: the operations running one needs from the GameServer tier, which
	///     owns the packet framing, the pet spawn and PetIIR assign, and the buff registry.
	/// </summary>
	/// <remarks>
	///     <see cref="SpellEffectService" /> depends on this narrow port rather than on the whole listener.
	/// </remarks>
	public interface ISpellEffectOutbound
	{
		/// <summary>
		///     Apply a spell to the player as a self-buff (ApplySpell, caster == subject).
		/// </summary>
		/// <param name="wireSpellId">
		///     The wire id ((base &lt;&lt; 8) | rank), the same form every ApplySpell send path uses.
		/// </param>
		/// <param name="walkChain">
		///     False applies the spell alone, leaving its 53051 rows unwalked - for a carrier whose rows are a
		///     candidate registry rather than "when this lands, also run X". See <see cref="FatalityService" />.
		/// </param>
		Task ApplySelfBuff(INetworkClient client, uint wireSpellId, bool walkChain = true);

		/// <summary>
		///     Summon a creature type as the player's pet.
		/// </summary>
		/// <param name="pointCost">
		///     The summoning spell's own <c>SummonPointCost</c>. It goes on the wire: the PetIIR assign's <c>f2</c>
		///     slot is what the client's pet-point bar counts, and the server is the only thing that ever states it
		///     (stat 0x1C8 is never sent).
		/// </param>
		/// <returns>
		///     False when no spawnable type matches the name - a content gap the caller reports rather than swallows.
		/// </returns>
		Task<bool> SummonPet(INetworkClient client, string template, bool combat, uint pointCost);

		/// <summary>
		///     Summon a pet that lives for <paramref name="duration" />, or extend the one already out, clamped to
		///     <paramref name="cap" /> when one is given.
		/// </summary>
		Task SummonTimedPet(INetworkClient client, string template, TimeSpan duration, TimeSpan? cap, uint pointCost);
	}
}
