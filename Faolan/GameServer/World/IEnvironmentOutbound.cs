using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.GameServer.Combat;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     The wire side of environmental damage - what <see cref="EnvironmentService" /> needs from the GameServer
	///     tier. A narrow port rather than the listener itself, like <see cref="IWorldOutbound" />: the service owns
	///     when the world hurts a player, the tier owns the framing.
	/// </summary>
	public interface IEnvironmentOutbound
	{
		/// <summary>
		///     The StatDamageIIR environmental form: a damage amount with no caster and no spell. Deliberately not the
		///     damage-number send - retail shows no floating combat number for a fall.
		/// </summary>
		/// <param name="displayAmount">
		///     In display units, as everything on <see cref="EntityVitals" /> is. The implementation scales it to the
		///     stat's wire value.
		/// </param>
		Task SendEnvironmentalDamage(INetworkClient client, uint targetHandle, uint displayAmount, uint statId, uint school);

		/// <summary>Push the player's 0x96B8DC59 vitals so the health bar moves.</summary>
		Task PushVitals(INetworkClient client, EntityVitals vitals);

		/// <summary>
		///     Apply a spell to the player as a self-buff - the fall debuff, or the hazard's own effect spell. Takes the
		///     wire id ((base &lt;&lt; 8) | rank), like every other ApplySpell path.
		/// </summary>
		Task ApplySelfBuff(INetworkClient client, uint wireSpellId);

		/// <summary>
		///     Unseat the player. The one dismount path, because retail ends every dismount with the same two records
		///     whatever caused it, so a no-mount volume reuses it rather than inventing a third.
		/// </summary>
		Task SendDismount(INetworkClient client);
	}
}
