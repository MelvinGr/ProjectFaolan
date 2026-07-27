using System.Threading.Tasks;
using Faolan.Core.Network;

namespace Faolan.GameServer.Combat
{
	// The wire (and world-state) side of the spellweaving FSM: everything SpellweaveService needs from outside
	// itself. Implemented by the GameServer listener, which owns the packet framing, the buff registry, the energy
	// loop and the chat dispatcher - so the service depends on this seven-method surface rather than the whole
	// listener god-class, the same decoupling IEconomyOutbound / ISpellEffectOutbound / IWorldOutbound give.
	//
	// It replaces SEVEN optional Func<>/Action<> constructor seams. Per-op delegates are the right first step when
	// one service needs one op (Gitea #200), but at seven they had stopped paying: the service carried 13 null
	// checks for seams that are never null - there is exactly one construction site and it passes all seven - so
	// every send was written as "if this happens to be wired", and a genuinely missing send would have been
	// indistinguishable from a deliberately-unwired one.
	public interface ISpellweaveOutbound
	{
		// Whether the caster is mounted right now. The trance roots and kneels the caster, so it cannot be entered
		// from the saddle.
		bool IsMounted(INetworkClient client);

		// Start the persistent energy tracking/regen loop, if it is not already running for this client.
		void EnsureEnergyLoop(INetworkClient client);

		// Apply a spell to the caster as a self-buff - the trance buffs and each component's own buff. Takes the WIRE
		// id ((base << 8) | rank).
		Task ApplyBuff(INetworkClient client, uint applyTo, uint applyFrom, uint wireSpellId);

		// The end-twin that removes one of those buffs on cancel/fire.
		Task RemoveBuff(INetworkClient client, uint applyTo, uint applyFrom, uint wireSpellId);

		// An on-screen system message to the caster.
		Task SendFeedback(INetworkClient client, string message);

		// DispatchUpdateSprintbar (0x200E) - the yellow energy bar that drains during the trance.
		Task SendSprintBar(INetworkClient client, uint energy);
	}
}
