using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Server.CsPlayerAgent;
using Faolan.Core.Network.Server.GameServer;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Quest
{
	// Quest sharing: offering a quest the player holds to their team-mates, and accepting one offered to them.
	//
	// The message set is docs/subsystems/quest-sharing.md. Unlike teams and trade, no capture performs a share, so
	// every body here is built from the client's own reader and handler rather than observed. The rules below are the
	// client's, recovered with it:
	//
	//   * the feedback codes are the two jump tables in GamecodeClient_t (see QuestShareFeedback),
	//   * "may not be shared" is QuestDefinition.QuestLineFlags bit 4 - the bit that greys the journal's Share button,
	//   * the range is the 60-unit test the share window's own tick applies before it drops out with
	//     "QuestSharePlayerMovedOutOfRange".
	//
	// A share is live state like a trade: it cannot outlive either connection, so nothing here is persisted. What is
	// persisted is the outcome, and only when the target accepts.
	public sealed class QuestSharingService
	{
		// How far a team-mate may be and still be offered the quest. The client applies this same distance while its
		// share window is open - the window's tick handler (FUN_01220bb0) measures the sharer every second and closes
		// with "QuestSharePlayerMovedOutOfRange" past 60.0 - so offering someone further away raises a window that
		// immediately cancels itself.
		public const float ShareRadius = 60f;

		// QuestDefinition.QuestLineFlags bit 4: this quest may not be shared. Proven in-client (see the column's own
		// note) - it is what disables the journal's Share button, and this is the server's half of the same rule.
		private const uint NotShareableFlag = 0x10;

		private readonly ICsPlayerAgentProvider _teams;
		private readonly ICharacterQuestRepository _quests;
		private readonly IWorldRepository _world;
		private readonly ILogger _logger;
		private readonly IQuestShareOutbound _outbound;
		private readonly QuestProgressionService _progression;

		// target -> the offer they have open. The accept and decline bodies name the other party, but what is granted
		// comes from here rather than from the body, so a forged ShareQuestAccepted cannot hand its sender an arbitrary
		// quest - the same rule the team accept and the trade start follow.
		private readonly ConcurrentDictionary<uint, PendingShare> _pendingByTarget = [];

		public QuestSharingService(ICharacterQuestRepository quests, IWorldRepository world, ICsPlayerAgentProvider teams, IQuestShareOutbound outbound, QuestProgressionService progression, ILogger logger)
		{
			_quests = quests;
			_world = world;
			_teams = teams;
			_outbound = outbound;
			_progression = progression;
			_logger = logger;
		}

		// c2s ShareQuest (0x204F): offer `questId` to every eligible team-mate, and answer the sharer with what
		// happened. Every exit sends the sharer exactly one global feedback, plus one individual feedback per team-mate
		// that failed for a reason worth naming.
		public async Task Share(uint sharerId, uint questId)
		{
			// The roster includes the sharer, so "solo" is a roster of 0 or 1 - the CS tier only mints a team once
			// someone joins.
			var roster = _teams.TeamMembersOf(sharerId).Where(id => id != sharerId).ToList();
			if (roster.Count == 0)
			{
				await _outbound.SendQuestShareGlobalFeedback(sharerId, QuestShareFeedback.NotInTeam);
				return;
			}

			// You can only share a quest you are actually on. A completed one is not shareable either - it is no longer
			// in the log the Share button lives in.
			var mine = await _quests.GetCharacterQuests(sharerId);
			var def = mine.Any(q => q.QuestId == questId && q.State == QuestState.Active)
				? await _world.GetQuestDefinition(questId)
				: null;
			if (def == null)
			{
				await _outbound.SendQuestShareGlobalFeedback(sharerId, QuestShareFeedback.MainQuestNotFound);
				return;
			}

			if ((def.QuestLineFlags & NotShareableFlag) != 0)
			{
				await _outbound.SendQuestShareGlobalFeedback(sharerId, QuestShareFeedback.MainQuestCanNotShare);
				return;
			}

			var sharerState = _outbound.LiveStateOf(sharerId);
			var legit = 0u;
			var outOfRange = 0u;

			foreach (var memberId in roster)
			{
				var member = _outbound.LiveStateOf(memberId);
				if (member == null)
					continue; // in the roster but not in this world process - nothing to offer, and no code says so

				var theirs = await _quests.GetCharacterQuests(memberId);
				if (theirs.Any(q => q.QuestId == questId && q.State == QuestState.Completed))
				{
					await _outbound.SendQuestShareIndividualFeedback(sharerId, QuestShareFeedback.AlreadyFinished, member.Name);
					continue;
				}

				// Already on it: not a legit target, and the client has no code for it (the enum's twelve are exhausted
				// by the two jump tables). Silence is the honest answer rather than borrowing a code that means
				// something else.
				if (theirs.Any(q => q.QuestId == questId))
					continue;

				if (!InRange(sharerState, member))
				{
					outOfRange++;
					continue;
				}

				if (await _outbound.SendQuestShareRequest(memberId, sharerId, questId, def.Name ?? ""))
				{
					_pendingByTarget[memberId] = new PendingShare(sharerId, questId);
					legit++;
				}
			}

			// The counts, in the order the sharer reads them: what went out, then what could not. NoLegitTargets is the
			// "nobody at all" case, and it is exclusive with the other two.
			if (legit > 0)
				await _outbound.SendQuestShareGlobalFeedback(sharerId, QuestShareFeedback.LegitTargets, legit);
			if (outOfRange > 0)
				await _outbound.SendQuestShareGlobalFeedback(sharerId, QuestShareFeedback.OutOfRangeTargets, outOfRange);
			if (legit == 0 && outOfRange == 0)
				await _outbound.SendQuestShareGlobalFeedback(sharerId, QuestShareFeedback.NoLegitTargets);

			_logger.LogInformation("QuestShare: {Sharer:X8} offered quest {QuestId} to {Legit} of {Roster} team-mate(s), {OutOfRange} out of range.", sharerId, questId, legit, roster.Count,
				outOfRange);
		}

		// c2s ShareQuestAccepted (0x2051): the target pressed Yes. The quest granted is the one the server offered, not
		// the one the body names - the body's echo is only checked against it.
		public async Task Accept(uint accepterId, uint echoedQuestId)
		{
			if (!_pendingByTarget.TryRemove(accepterId, out var pending))
			{
				_logger.LogWarning("QuestShare: ShareQuestAccepted from {Character:X8} with no pending offer.", accepterId);
				return;
			}

			if (echoedQuestId != pending.QuestId)
			{
				_logger.LogWarning("QuestShare: {Character:X8} accepted quest {Echoed} but was offered {Offered}; granting neither.", accepterId, echoedQuestId, pending.QuestId);
				return;
			}

			var client = _outbound.ClientFor(accepterId);
			if (client == null)
				return;

			// Retail has no "accepted" feedback code - the twelve are exhausted by declined/busy and the counts - so
			// the sharer is told nothing. The target simply gets the quest.
			if (await _progression.Grant(client, pending.QuestId, $"a share from {pending.SharerId:X8}") is { } def)
				await _progression.SendGrantPackets(client, def);
		}

		// c2s ShareQuestDeclined (0x2050) / ShareQuestBusy (0x2052): the target pressed No, or their client refused to
		// raise the window at all. Both relay one individual feedback back to the sharer; only the code differs.
		public Task Decline(uint declinerId, bool busy)
		{
			if (!_pendingByTarget.TryRemove(declinerId, out var pending))
				return Task.CompletedTask;

			var name = _outbound.LiveStateOf(declinerId)?.Name ?? "";
			return _outbound.SendQuestShareIndividualFeedback(pending.SharerId, busy
				? QuestShareFeedback.PlayerBusy
				: QuestShareFeedback.QuestDeclined, name);
		}

		// The player left the world: drop any offer aimed at them, so a later reconnect cannot accept a stale one.
		public void Forget(uint characterId)
		{
			_pendingByTarget.TryRemove(characterId, out _);
		}

		// Same playfield, and within the window's own 60-unit leash. A member the world does not place (either side
		// missing) is out of range rather than in it - the permissive reading would offer a quest to someone the
		// client then immediately drops.
		private static bool InRange(CharacterLiveState? sharer, CharacterLiveState member)
		{
			if (sharer == null || sharer.MapId != member.MapId)
				return false;

			var dx = sharer.X - member.X;
			var dy = sharer.Y - member.Y;
			var dz = sharer.Z - member.Z;

			return MathF.Sqrt(dx * dx + dy * dy + dz * dz) <= ShareRadius;
		}

		private sealed record PendingShare(uint SharerId, uint QuestId);
	}
}
