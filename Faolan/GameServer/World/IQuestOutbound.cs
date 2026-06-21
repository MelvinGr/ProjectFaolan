using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;

namespace Faolan.GameServer.World
{
	// The narrow quest wire-send port the QuestProgressionService needs from the listener side (WorldBroadcaster
	// implements it). Mirrors the IEconomyOutbound / IWorldOutbound pattern (CLAUDE.md "narrow port"): the supersession
	// engine drives quest STATE and reaches the three quest packets through this seam instead of taking the whole
	// broadcaster, which keeps its dependency surface explicit and makes the call SEQUENCE unit-testable with a
	// recording fake.
	public interface IQuestOutbound
	{
		// QuestGoal (0x0774C8C1): the objective counter for `questId` is now `count`.
		Task SendQuestGoal(INetworkClient client, uint questId, uint goalId, uint count);

		// QuestIIR (0x97E02314): add/update `questId` in the journal at `state` (StateActive 2 / StateCompleted 4).
		Task SendQuestIir(INetworkClient client, uint questId, uint state);

		// QuestFullUpdate (0x63268258): one quest's full detail - the supersession packet that swaps the displayed quest.
		Task SendQuestFullUpdate(INetworkClient client, QuestDefinition def, uint state = WorldQuest.FullUpdateStateActive);
	}
}
