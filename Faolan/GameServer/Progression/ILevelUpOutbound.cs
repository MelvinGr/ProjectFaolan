using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.GameServer.Combat;

namespace Faolan.GameServer.Progression
{
	// The wire side of a level-up: the operations the ding burst needs from the listener. Implemented by the
	// GameServer listener (which owns the packet framing, the announced 0x2052 ability grant and the
	// DispatchLearnSpell send), so LevelUpService depends on this narrow port rather than on the whole listener - or,
	// as it did before the extraction, on three loose `Func<>` seams threaded through CombatService's constructor.
	//
	// The vitals reseed is on this port for the same reason: it lives on CombatService (the world model owns a
	// player's live vitals), and routing it through the listener is what keeps the two services from depending on
	// each other - CombatService drives LevelUpService on a kill, never the reverse.
	public interface ILevelUpOutbound
	{
		// Rebuild the player's live vitals MAXIMA from the new (Class, Level) - a ding raises the ceiling and refills
		// the pools. Forwards to CombatService.ReseedFromMaxStats.
		Task<EntityVitals> ReseedVitals(INetworkClient client);

		// The level-derived rating quad (#685). Shared with the combat toggle (#505), which is why it stays a listener
		// op rather than being rebuilt here.
		Task SendCombatRatings(INetworkClient client);

		// Grant an ability as an ANNOUNCED 0x2052 - the "you have learned a new ability" toast retail pops at a ding.
		Task AnnounceAbility(INetworkClient client, Ability ability);

		// Teach a spell live (DispatchLearnSpell 0x200A). Persistence is the caller's job.
		Task LearnSpell(INetworkClient client, uint spellId);
	}
}
