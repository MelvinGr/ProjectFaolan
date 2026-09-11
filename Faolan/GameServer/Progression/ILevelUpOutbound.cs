using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.GameServer.Combat;

namespace Faolan.GameServer.Progression
{
	// The wire side of a level-up: the operations the ding burst needs. LevelUpService depends on this narrow port
	// rather than on the whole tier's wire surface.
	//
	// The vitals reseed is on this port for the same reason: it lives on CombatService, because the world model owns a
	// player's live vitals, and routing it through the port is what keeps the two services from depending on each
	// other - CombatService drives LevelUpService on a kill, never the reverse.
	public interface ILevelUpOutbound
	{
		// Rebuild the player's live vitals maxima from the new (Class, Level) - a ding raises the ceiling and refills
		// the pools. Forwards to CombatService.ReseedFromMaxStats.
		Task<EntityVitals> ReseedVitals(INetworkClient client);

		// The level-derived rating quad. Shared with the combat toggle, which is why it stays a tier-level op rather
		// than being rebuilt here.
		Task SendCombatRatings(INetworkClient client);

		// Grant an ability as an announced 0x2052 - the "you have learned a new ability" toast retail pops at a ding.
		Task AnnounceAbility(INetworkClient client, Ability ability);

		// Teach a spell live (DispatchLearnSpell 0x200A). Persistence is the caller's job.
		Task LearnSpell(INetworkClient client, uint spellId);
	}
}
