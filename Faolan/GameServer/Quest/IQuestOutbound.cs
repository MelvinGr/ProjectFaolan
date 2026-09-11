using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Quest;

namespace Faolan.GameServer.Quest
{
	// The narrow quest wire-send port QuestProgressionService needs. The same shape as IEconomyOutbound and
	// IWorldOutbound: the supersession engine drives quest state and reaches the quest packets through this seam
	// instead of taking the whole broadcaster, which keeps its dependency surface explicit and makes the call sequence
	// unit-testable with a recording fake.
	public interface IQuestOutbound
	{
		// Applies the after-quest world-object effects for the player (WorldObject.EffectQuestId) - for example dropping
		// the beach chain-wall collision the instant the escort quest becomes active. Data-driven, and a no-op when
		// nothing matches.
		Task ApplyQuestWorldEffects(INetworkClient client);

		// Drops the player's cached pending ReachArea goals (WorldEntity.PendingReachGoals) so the next move reloads
		// them from the database. Called after a supersession changes the active quest set, because a newly granted
		// stage may carry a ReachArea goal (Shipwrecked II's "find the gate key", III's "enter the city") that the
		// cache would otherwise miss until relog.
		void InvalidateReachGoals(INetworkClient client);

		// Re-sends the named NPC's quest marker to the player in place, by name because handles are session-volatile.
		// Used after a chain stage supersedes to a talk goal while that NPC is away (looting Tuut's key activates the
		// talk-Casilda stage), so her '?' appears immediately rather than on the next 0x2020 poll or re-stream.
		Task RefreshQuestMarkerFor(INetworkClient client, string npcName, uint? npcTemplateId = null);

		// The character's quest rows were written: drop their cached quest-marker state so the next marker - including
		// the refresh a write is usually followed by - reads the new rows. Every writer calls this.
		void ForgetQuestMarkers(uint characterId);

		// A goal that springs an ambush just advanced for the first time: put the ambusher in the world beside the
		// player after the definition's delay. A no-op when the player is no longer there to ambush.
		void ScheduleQuestAmbush(INetworkClient client, NpcAmbush ambush);

		// The player's quest state changed, so re-evaluate quest-gated NPC visibility: reload their active quest set and
		// run an interest pass, so newly qualified gated NPCs (Scavenger Tuut) spawn and no-longer-qualified ones
		// despawn in place without waiting for the player or the NPC to move.
		Task RefreshQuestVisibility(INetworkClient client);

		// QuestFullUpdate (0x63268258): one quest's full detail - the supersession packet that swaps the displayed quest.
		// `level` defaults to the quest's harvested QuestDefinition.Level, a per-quest constant; pass an explicit value
		// only to override.
		Task SendQuestFullUpdate(INetworkClient client, QuestDefinition def, uint? level = null);

		// QuestLogList / MainQuestUpdate (0x504B7924): the active-quest list that populates the J-panel, with
		// `trackedDef` highlighted and `activeQuestIds` holding every active quest, tracked one first. Paired with
		// SendQuestFullUpdate on a grant - the detail alone leaves the log empty.
		Task SendQuestLogList(INetworkClient client, QuestDefinition trackedDef, IReadOnlyList<uint> activeQuestIds);

		// The J-panel remove - the same record with `Operation = Remove` and an empty list, naming only the line whose
		// section goes away. The third packet of a quest drop.
		Task SendQuestLogRemove(INetworkClient client, uint questLineId);

		// QuestGoal (0x0774C8C1): tell the client what just happened to `goalId` - met, partially advanced, or closed
		// so the quest can move to its next goal. It carries no running count; the journal holds that.
		Task SendQuestGoal(INetworkClient client, uint questId, uint goalId, QuestGoalUpdate update);

		// QuestIIR (0x97E02314): add/update `questId` in the journal at `state` (StateActive 2 / StateCompleted 4).
		Task SendQuestIir(INetworkClient client, uint questId, uint state);

		// QuestRewardNotifyIIR (0x8BB2799C) with options: the pick-one-of-N reward window a completing quest presents.
		// Each entry carries the item's own ProtoDecryptionKey, which is what lets the client render it.
		Task SendQuestRewardOffer(INetworkClient client, uint questId, IReadOnlyList<QuestRewardEntry> options);

		// The same IIR with no options - the confirm that closes the reward window, echoing the chosen index.
		Task SendQuestRewardConfirm(INetworkClient client, uint questId, uint index);

		// The quest-driven companion trigger: the NPC of template id `templateId` (the Escort goal's TrailingRefId)
		// follows, or stops following, the player. Keyed by id rather than name, so the escort stays data-driven. A
		// no-op when no such NPC is loaded; returns true when applied.
		bool SetNpcFollowByTemplate(uint mapId, uint templateId, uint playerHandle, bool follow);
	}
}
