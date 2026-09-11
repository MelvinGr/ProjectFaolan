using System.Threading.Tasks;
using Faolan.Core.Network;

namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     The wire and world-state side of the spellweaving FSM: everything <see cref="SpellweaveService" /> needs
	///     from outside itself.
	/// </summary>
	/// <remarks>
	///     Implemented in the GameServer tier, which owns the packet framing, the buff registry, the energy loop and
	///     the chat dispatcher, so the service depends on this seven-method surface rather than on the listener.
	/// </remarks>
	public interface ISpellweaveOutbound
	{
		/// <summary>
		///     Whether the caster is mounted right now. The trance roots and kneels the caster, so it cannot be entered
		///     from the saddle.
		/// </summary>
		bool IsMounted(INetworkClient client);

		/// <summary>
		///     Start the persistent energy tracking and regen loop, if it is not already running for this client.
		/// </summary>
		void EnsureEnergyLoop(INetworkClient client);

		/// <summary>
		///     Apply a spell to the caster as a self-buff - the trance buffs and each component's own buff.
		/// </summary>
		/// <param name="wireSpellId">The wire id ((base &lt;&lt; 8) | rank).</param>
		Task ApplyBuff(INetworkClient client, uint applyTo, uint applyFrom, uint wireSpellId);

		/// <summary>The end-twin that removes one of those buffs on cancel or fire.</summary>
		Task RemoveBuff(INetworkClient client, uint applyTo, uint applyFrom, uint wireSpellId);

		/// <summary>An on-screen system message to the caster.</summary>
		Task SendFeedback(INetworkClient client, string message);

		/// <summary>
		///     Retail's own refusal for a weave attempted from the saddle: the ChatTextIIR carrying LDB 100/562
		///     <c>'%s must not have %s running.'</c>, parameterised with "You" and the mount buff.
		/// </summary>
		/// <remarks>
		///     Separate from <see cref="SendFeedback" /> because it is a different channel - the world chat line retail
		///     sends, not a ChatServer system message - and because it is a captured literal rather than a composed
		///     string.
		/// </remarks>
		Task SendMountedRefusal(INetworkClient client);

		/// <summary>
		///     DispatchUpdateSprintbar (0x200E) - the yellow energy bar that drains during the trance.
		/// </summary>
		Task SendSprintBar(INetworkClient client, uint energy);
	}
}
