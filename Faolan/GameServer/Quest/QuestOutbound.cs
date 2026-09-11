using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.GameServer.World;

namespace Faolan.GameServer.Quest
{
	// IQuestOutbound: the narrow quest port QuestProgressionService and QuestRewardService take, as its own class
	// rather than as an interface on WorldBroadcaster.
	//
	// It forwards to two owners because the port spans two subjects. Nine members are quest wire and go to
	// QuestBroadcaster; three need the live world and stay on the broadcaster - a quest-state change re-running an
	// interest pass (RefreshQuestVisibility), an ambusher being put in the zone (ScheduleQuestAmbush), and an escort
	// NPC being told to follow (SetNpcFollowByTemplate). Splitting the interface instead would hand the two quest
	// services a second port each for no gain; this is the shape WorldOutbound has, forwarding to four owners.
	public sealed class QuestOutbound : IQuestOutbound
	{
		private readonly WorldBroadcaster _broadcaster;
		private readonly NpcBroadcaster _npcs;
		private readonly QuestBroadcaster _quests;

		public QuestOutbound(QuestBroadcaster quests, WorldBroadcaster broadcaster, NpcBroadcaster npcs)
		{
			_quests = quests;
			_broadcaster = broadcaster;
			_npcs = npcs;
		}

		public Task ApplyQuestWorldEffects(INetworkClient client)
		{
			return _quests.ApplyQuestWorldEffects(client);
		}

		public void InvalidateReachGoals(INetworkClient client)
		{
			_quests.InvalidateReachGoals(client);
		}

		public void ForgetQuestMarkers(uint characterId)
		{
			_quests.ForgetMarkerContext(characterId);
		}

		public Task RefreshQuestMarkerFor(INetworkClient client, string npcName, uint? npcTemplateId = null)
		{
			return _quests.RefreshQuestMarkerFor(client, npcName, npcTemplateId);
		}

		public Task SendQuestFullUpdate(INetworkClient client, QuestDefinition def, uint? level = null)
		{
			return _quests.SendQuestFullUpdate(client, def, level);
		}

		public Task SendQuestLogList(INetworkClient client, QuestDefinition trackedDef, IReadOnlyList<uint> activeQuestIds)
		{
			return QuestBroadcaster.SendQuestLogList(client, trackedDef, activeQuestIds);
		}

		public Task SendQuestLogRemove(INetworkClient client, uint questLineId)
		{
			return QuestBroadcaster.SendQuestLogRemove(client, questLineId);
		}

		public Task SendQuestGoal(INetworkClient client, uint questId, uint goalId, QuestGoalUpdate update)
		{
			return QuestBroadcaster.SendQuestGoal(client, questId, goalId, update);
		}

		public Task SendQuestIir(INetworkClient client, uint questId, uint state)
		{
			return QuestBroadcaster.SendQuestIir(client, questId, state);
		}

		public Task SendQuestRewardOffer(INetworkClient client, uint questId, IReadOnlyList<QuestRewardEntry> options)
		{
			return QuestBroadcaster.SendQuestRewardOffer(client, questId, options);
		}

		public Task SendQuestRewardConfirm(INetworkClient client, uint questId, uint index)
		{
			return QuestBroadcaster.SendQuestRewardConfirm(client, questId, index);
		}

		// --- the three that need the live world, so they stay with it.

		public void ScheduleQuestAmbush(INetworkClient client, NpcAmbush ambush)
		{
			_broadcaster.ScheduleQuestAmbush(client, ambush);
		}

		public Task RefreshQuestVisibility(INetworkClient client)
		{
			return _broadcaster.RefreshQuestVisibility(client);
		}

		public bool SetNpcFollowByTemplate(uint mapId, uint templateId, uint playerHandle, bool follow)
		{
			return _npcs.SetNpcFollowByTemplate(mapId, templateId, playerHandle, follow);
		}
	}
}
