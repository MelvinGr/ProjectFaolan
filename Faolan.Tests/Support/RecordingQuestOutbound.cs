using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.GameServer.Quest;

namespace Faolan.Tests.Support
{
	// An IQuestOutbound that records what the quest engine tells the client, as one ordered `Calls` log ("log-list",
	// "full-update", "goal <id> <update>", "quest state <n>") plus the ambushes it schedules. One class for every
	// gate; a gate that cares about one kind filters.
	public sealed class RecordingQuestOutbound : IQuestOutbound
	{
		public List<string> Calls { get; } = [];
		public List<uint> Ambushes { get; } = [];

		public Task SendQuestLogList(INetworkClient client, QuestDefinition trackedDef, IReadOnlyList<uint> activeQuestIds)
		{
			Calls.Add("log-list");
			return Task.CompletedTask;
		}

		public Task SendQuestFullUpdate(INetworkClient client, QuestDefinition def, uint? level = null)
		{
			Calls.Add("full-update");
			return Task.CompletedTask;
		}

		public Task SendQuestGoal(INetworkClient client, uint questId, uint goalId, QuestGoalUpdate update)
		{
			Calls.Add($"goal {goalId} {update}");
			return Task.CompletedTask;
		}

		public Task SendQuestIir(INetworkClient client, uint questId, uint state)
		{
			Calls.Add($"quest state {state}");
			return Task.CompletedTask;
		}

		public void ScheduleQuestAmbush(INetworkClient client, NpcAmbush ambush)
		{
			Ambushes.Add(ambush.NpcTemplateId);
		}

		public Task ApplyQuestWorldEffects(INetworkClient client) => Task.CompletedTask;
		public void InvalidateReachGoals(INetworkClient client) { }
		public void ForgetQuestMarkers(uint characterId) { }
		public Task RefreshQuestMarkerFor(INetworkClient client, string npcName, uint? npcTemplateId = null) => Task.CompletedTask;
		public Task RefreshQuestVisibility(INetworkClient client) => Task.CompletedTask;
		public Task SendQuestLogRemove(INetworkClient client, uint questLineId) => Task.CompletedTask;
		public Task SendQuestRewardOffer(INetworkClient client, uint questId, IReadOnlyList<QuestRewardEntry> options) => Task.CompletedTask;
		public Task SendQuestRewardConfirm(INetworkClient client, uint questId, uint index) => Task.CompletedTask;
		public bool SetNpcFollowByTemplate(uint mapId, uint templateId, uint playerHandle, bool follow) => false;
	}
}
