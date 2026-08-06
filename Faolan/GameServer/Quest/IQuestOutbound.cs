using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;

namespace Faolan.GameServer.Quest
{
	// The narrow quest wire-send port the QuestProgressionService needs from the listener side (WorldBroadcaster
	// implements it). Mirrors the IEconomyOutbound / IWorldOutbound pattern (CLAUDE.md "narrow port"): the supersession
	// engine drives quest STATE and reaches the three quest packets through this seam instead of taking the whole
	// broadcaster, which keeps its dependency surface explicit and makes the call SEQUENCE unit-testable with a
	// recording fake.
	public interface IQuestOutbound
	{
		// #262: apply the after-quest world-object effects for the player (WorldObject.EffectQuestId) - e.g. drop the
		// beach chain-wall collision the instant the escort quest becomes active. Data-driven; no-op if nothing matches.
		Task ApplyQuestWorldEffects(INetworkClient client);

		// #262: drop the player's cached pending ReachArea goals (WorldEntity.PendingReachGoals) so the next move
		// reloads them from the DB. Called after a supersession changes the active quest set - a newly-granted stage
		// may carry a ReachArea goal (e.g. Shipwrecked II's "find the gate key", III's "enter the city") that the
		// once-lazy cache would otherwise miss until relog.
		void InvalidateReachGoals(INetworkClient client);

		// #267: re-send the named NPC's quest marker to the player in-place (handles are session-volatile, so by NAME).
		// Used after a chain stage supersedes to a TALK goal while that NPC is away (e.g. looting Tuut's key activates
		// the talk-Casilda stage), so her '?' appears immediately instead of only on the next 0x2020 poll / re-stream.
		Task RefreshQuestMarkerByName(INetworkClient client, string npcName);

		// #528: a goal that springs an AMBUSH just advanced for the first time - put the ambusher in the world beside
		// the player after the definition's delay. No-op when the player is no longer there to ambush.
		void ScheduleQuestAmbush(INetworkClient client, NpcAmbush ambush);

		// #261: the player's quest state changed, so re-evaluate quest-gated NPC visibility - reload their active quest
		// set and run an interest pass so newly-qualified gated NPCs (e.g. Scavenger Tuut) spawn and no-longer-qualified
		// ones despawn in-place, without waiting for the player or NPC to move.
		Task RefreshQuestVisibility(INetworkClient client);

		// QuestFullUpdate (0x63268258): one quest's full detail - the supersession packet that swaps the displayed quest.
		// `level` defaults to the quest's harvested QuestDefinition.Level (per-quest constant, #352); pass an explicit
		// value only to override.
		Task SendQuestFullUpdate(INetworkClient client, QuestDefinition def, uint? level = null);

		// QuestLogList / MainQuestUpdate (0x504B7924): the active-quest LIST that POPULATES the J-panel, with
		// `trackedDef` highlighted and `activeQuestIds` holding every active quest, tracked one first. Paired with
		// SendQuestFullUpdate on a grant - the detail alone leaves the log empty.
		Task SendQuestLogList(INetworkClient client, QuestDefinition trackedDef, IReadOnlyList<uint> activeQuestIds);

		// QuestGoal (0x0774C8C1): tell the client what just happened to `goalId` - met, partially advanced, or closed
		// so the quest can move to its next goal. It carries no running count; the journal holds that (#525).
		Task SendQuestGoal(INetworkClient client, uint questId, uint goalId, QuestGoalUpdate update);

		// QuestIIR (0x97E02314): add/update `questId` in the journal at `state` (StateActive 2 / StateCompleted 4).
		Task SendQuestIir(INetworkClient client, uint questId, uint state);

		// #262: the quest-driven companion trigger - the NPC of TEMPLATE id `templateId` (the Escort goal's TrailingRefId)
		// follows (or stops following) the player. Id-based (data-driven escort, not a hard-coded name). No-op if no such
		// NPC is loaded; returns true if applied.
		bool SetNpcFollowByTemplate(uint mapId, uint templateId, uint playerHandle, bool follow);
	}
}
