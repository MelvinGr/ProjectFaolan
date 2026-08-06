using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.GameServer.Combat;

namespace Faolan.GameServer.World
{
	// The wire side of environmental damage (#495/#504) - what EnvironmentService needs from the listener. Narrow port
	// rather than the listener itself, same pattern as IWorldOutbound / IEconomyOutbound: the service owns WHEN the
	// world hurts a player, the listener owns the framing.
	public interface IEnvironmentOutbound
	{
		// The StatDamageIIR ENVIRONMENTAL form (CombatEffectBuilder.EnvironmentalDamage): a damage amount with no
		// caster and no spell. Deliberately NOT SendDamageNumber - retail sends no floating combat number for a fall.
		Task SendEnvironmentalDamage(INetworkClient client, uint targetHandle, uint amount, uint statId, uint school);

		// Push the player's 0x96B8DC59 vitals so the health bar moves.
		Task PushVitals(INetworkClient client, EntityVitals vitals);

		// Apply a spell to the player as a self-buff (the fall debuff, the hazard's own effect spell). Takes the WIRE
		// id ((base << 8) | rank) like every other ApplySpell path.
		Task ApplySelfBuff(INetworkClient client, uint wireSpellId);

		// Unseat the player (#681). The listener's ONE dismount path, because retail ends every dismount with the same
		// two records whatever caused it - so a no-mount volume reuses it rather than inventing a third.
		Task SendDismount(INetworkClient client);
	}
}
