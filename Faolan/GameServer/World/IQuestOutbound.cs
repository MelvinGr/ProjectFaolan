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

		// #262: make the named NPC in `mapId`'s zone follow (or stop following) the given player - the quest-driven
		// companion trigger (Casilda after her rescue). No-op if no such NPC is loaded; returns true if applied.
		bool SetNpcFollowByName(uint mapId, string npcName, uint playerHandle, bool follow);

		// #267: lift the Tortage beach progression gate (the "invisible wall") for the player by sending the ShacklesOff
		// destiny Scry step - the client drops the confinement when Casilda is unchained. Sent the moment she's freed
		// (the talk-goal completion); the create-world destiny scene raised the wall, this is the matching lift.
		Task LiftBeachGate(INetworkClient client);

		// #261: the player's quest state changed, so re-evaluate quest-gated NPC visibility - reload their active quest
		// set and run an interest pass so newly-qualified gated NPCs (e.g. Scavenger Tuut) spawn and no-longer-qualified
		// ones despawn in-place, without waiting for the player or NPC to move.
		Task RefreshQuestVisibility(INetworkClient client);
	}
}
