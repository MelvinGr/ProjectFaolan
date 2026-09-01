using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.GameServer.Quest;

namespace Faolan.Tests.Support
{
	// `IQuestOutbound` with every member a no-op, for the service tests that only care about one or two of them - the
	// quest twin of NoOpEconomyOutbound, and added for the reason that one records: the port is a growing surface, and
	// each addition breaks every hand-rolled double at once. Adding SendQuestRewardOffer/SendQuestRewardConfirm (#815)
	// failed three unrelated test classes that have no opinion about quest rewards.
	//
	// ⚠️ No-ops rather than throws, same rationale: a test wires this up to exercise a SERVICE, and a service
	// legitimately sends packets the test is not about. Throwing would make every such test assert the whole port.
	public class NoOpQuestOutbound : IQuestOutbound
	{
		public virtual Task ApplyQuestWorldEffects(INetworkClient client)
		{
			return Task.CompletedTask;
		}

		public virtual void InvalidateReachGoals(INetworkClient client)
		{
		}

		public virtual Task RefreshQuestMarkerFor(INetworkClient client, string npcName, uint? npcTemplateId = null)
		{
			return Task.CompletedTask;
		}

		public virtual void ScheduleQuestAmbush(INetworkClient client, NpcAmbush ambush)
		{
		}

		public virtual Task RefreshQuestVisibility(INetworkClient client)
		{
			return Task.CompletedTask;
		}

		public virtual Task SendQuestFullUpdate(INetworkClient client, QuestDefinition def, uint? level = null)
		{
			return Task.CompletedTask;
		}

		public virtual Task SendQuestLogList(INetworkClient client, QuestDefinition trackedDef, IReadOnlyList<uint> activeQuestIds)
		{
			return Task.CompletedTask;
		}

		public virtual Task SendQuestLogRemove(INetworkClient client, uint questLineId)
		{
			return Task.CompletedTask;
		}

		public virtual Task SendQuestGoal(INetworkClient client, uint questId, uint goalId, QuestGoalUpdate update)
		{
			return Task.CompletedTask;
		}

		public virtual Task SendQuestIir(INetworkClient client, uint questId, uint state)
		{
			return Task.CompletedTask;
		}

		public virtual Task SendQuestRewardOffer(INetworkClient client, uint questId, IReadOnlyList<QuestRewardEntry> options)
		{
			return Task.CompletedTask;
		}

		public virtual Task SendQuestRewardConfirm(INetworkClient client, uint questId, uint index)
		{
			return Task.CompletedTask;
		}

		public virtual bool SetNpcFollowByTemplate(uint mapId, uint templateId, uint playerHandle, bool follow)
		{
			return false;
		}
	}
}
