using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs;

namespace Faolan.GameServer.World
{
	// The narrow quest wire-send port the QuestProgressionService needs from the listener side (WorldBroadcaster
	// implements it). Mirrors the IEconomyOutbound / IWorldOutbound pattern (CLAUDE.md "narrow port"): the supersession
	// engine drives quest STATE and reaches the three quest packets through this seam instead of taking the whole
	// broadcaster, which keeps its dependency surface explicit and makes the call SEQUENCE unit-testable with a
	// recording fake.
	public interface IQuestOutbound
	{
		// #267: drop the beach "invisible wall" (the Casilda chain decor's collision) for the player by sending the
		// SimpleDynel visual-change retail sends the instant Casilda is freed on the beach (talk-goal completion).
		Task DropBeachWall(INetworkClient client);

		// #267: re-send the named NPC's quest marker to the player in-place (handles are session-volatile, so by NAME).
		// Used after a chain stage supersedes to a TALK goal while that NPC is away (e.g. looting Tuut's key activates
		// the talk-Casilda stage), so her '?' appears immediately instead of only on the next 0x2020 poll / re-stream.
		Task RefreshQuestMarkerByName(INetworkClient client, string npcName);

		// #261: the player's quest state changed, so re-evaluate quest-gated NPC visibility - reload their active quest
		// set and run an interest pass so newly-qualified gated NPCs (e.g. Scavenger Tuut) spawn and no-longer-qualified
		// ones despawn in-place, without waiting for the player or NPC to move.
		Task RefreshQuestVisibility(INetworkClient client);

		// QuestFullUpdate (0x63268258): one quest's full detail - the supersession packet that swaps the displayed quest.
		Task SendQuestFullUpdate(INetworkClient client, QuestDefinition def, uint state = WorldQuest.FullUpdateStateActive);

		// QuestGoal (0x0774C8C1): the objective counter for `questId` is now `count`.
		Task SendQuestGoal(INetworkClient client, uint questId, uint goalId, uint count);

		// QuestIIR (0x97E02314): add/update `questId` in the journal at `state` (StateActive 2 / StateCompleted 4).
		Task SendQuestIir(INetworkClient client, uint questId, uint state);

		// #262: make the named NPC in `mapId`'s zone follow (or stop following) the given player - the quest-driven
		// companion trigger (Casilda after her rescue). No-op if no such NPC is loaded; returns true if applied.
		bool SetNpcFollowByName(uint mapId, string npcName, uint playerHandle, bool follow);
	}
}
