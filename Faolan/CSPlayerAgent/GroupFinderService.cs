using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.GroupFinder;
using Faolan.Core.Network.Server.GameServer;
using Microsoft.Extensions.Logging;

namespace Faolan.CSPlayerAgent
{
	// The group-finder signup flow (#511), built message-for-message from the one capture that walks it end to end
	// (chat-annotated-4, t=1061..1104 - docs/subsystems/playfield-instance-ownership.md has the annotated trace):
	//
	//   c RequestNeededRole(activity)      -> s DispatchGroupFinderReceivedNeededRole
	//   c QueueGroupFinder(bits, _, priv)  -> s DispatchGroupFinderSignupFailed        (quest log at the cap)
	//                                      -> s DispatchGroupFinderRoleSelect          (accepted; no-arg, #801)
	//   c SelectGroupFinderRoles(roles)    -> s DispatchGroupFinderSignupSuccess + DispatchGroupFinderReadyCheck
	//   c ReadyGroupFinder(true, _)        -> s DispatchGroupFinderReadyCheckStatus + DispatchCharacterLookingForTeamStatus
	//                                         + DispatchYouJoinedTeam (the team forms - solo = the annotated
	//                                           "entered as private team") + DispatchGroupFinderActivityStarted
	//                                      -> the world tier teleports into the activity's playfield, which the
	//                                         team now OWNS (#512: the proxy's owner pair is (0xDEA9, teamId))
	//
	// A composed collaborator of the CsPlayerAgent listener, like TeamService/RaidService. Matchmaking proper is
	// NOT modelled - every signup runs the solo/private-team path the capture shows; a queuer who is already in a
	// team takes that team along instead of a fresh solo one.
	public sealed class GroupFinderService(ILogger logger, IDatabaseRepository database, TeamService teams, IGameServerDispatcher gameServerDispatcher)
	{
		// RDB 1000516 `Quest_MaxQuestPerPremiumPlayer` = 45 (the free-player twin is 30). Every account here is
		// premium, so the finder refuses a signup at 45 held quests - the capture's refusal cleared after deleting
		// exactly one, so the gate is >= the cap, not >.
		internal const int QuestLogCap = 45;

		// The roles word DispatchGroupFinderReceivedNeededRole carried for the captured activity. What the finder
		// "still needs" is matchmaking state we do not model, so every activity advertises the captured value.
		private const uint CapturedNeededRoles = 0x1;

		// Activity id -> (playfield, arrival). RDB 1000627 names the six-man dungeon playfields and each record's
		// own [x][y][z][heading] destination, but the per-category ActivityId->record remap at the table's tail is
		// still un-walked - so only the CAPTURED pair is mapped: activity 38 = The Amphitheatre of Karutonia (1092),
		// arrival (112, 70, 39) straight off the captured N3Teleport. An unmapped activity completes the signup flow
		// and logs instead of teleporting, rather than guessing a destination retail never confirmed.
		private static readonly ConcurrentDictionary<uint, (uint MapId, System.Numerics.Vector3 Arrival)> ActivityDestinations = new(
			new[] { new System.Collections.Generic.KeyValuePair<uint, (uint, System.Numerics.Vector3)>(38, (1092u, new System.Numerics.Vector3(112.0f, 70.0f, 39.0f))) });

		// characterId -> the signup that passed the quest gate, held until the ready check resolves it.
		private readonly ConcurrentDictionary<uint, QueueGroupFinderRequest> _pendingSignups = [];

		public async Task HandleRequestNeededRole(INetworkClient client, uint activityId)
		{
			await Send(client, CSPlayerInterfaceMethods.DispatchGroupFinderReceivedNeededRole, GroupFinderNeededRoleCodec.Build(activityId, CapturedNeededRoles));
		}

		public async Task HandleQueue(INetworkClient client, QueueGroupFinderRequest request)
		{
			var characterId = client.Session.Character.Id;

			// The gate the capture isolates: the identical queue body is refused at the cap and accepted one deleted
			// quest later, so nothing in the request decides it - only the quest log's size.
			var heldQuests = (await database.GetCharacterQuests(characterId, QuestState.Active)).Length;
			if (heldQuests >= QuestLogCap)
			{
				logger.LogInformation("GroupFinder: {CharacterId:X8} holds {Held} quests (cap {Cap}); signup refused.", characterId, heldQuests, QuestLogCap);
				await Send(client, CSPlayerInterfaceMethods.DispatchGroupFinderSignupFailed,
					GroupFinderSignupFailedCodec.Build(new GroupFinderSignupFailed(GroupFinderSignupFailedCodec.TooManyQuests)));

				return;
			}

			_pendingSignups[characterId] = request;

			// The accept is the no-arg 0x20C3: the client opens its groupFinder_roleSelect window on it (#801) and
			// answers with SelectGroupFinderRoles.
			await Send(client, CSPlayerInterfaceMethods.DispatchGroupFinderRoleSelect, []);
		}

		public async Task HandleSelectRoles(INetworkClient client, uint roles)
		{
			var characterId = client.Session.Character.Id;
			if (!_pendingSignups.TryGetValue(characterId, out var signup))
			{
				logger.LogWarning("SelectGroupFinderRoles from {CharacterId:X8} with no pending signup; ignored.", characterId);
				return;
			}

			logger.LogInformation("GroupFinder: {CharacterId:X8} selected roles 0x{Roles:X} for activity {Activity}.", characterId, roles, ActivityOf(signup));

			await Send(client, CSPlayerInterfaceMethods.DispatchGroupFinderSignupSuccess,
				GroupFinderSignupSuccessCodec.Build(new GroupFinderSignupSuccess(signup.ActivityWords, false)));
			await Send(client, CSPlayerInterfaceMethods.DispatchGroupFinderReadyCheck, GroupFinderReadyCheckCodec.Build(ActivityOf(signup)));
		}

		public async Task HandleReady(INetworkClient client, bool ready, bool isTimeout)
		{
			var characterId = client.Session.Character.Id;
			if (!_pendingSignups.TryRemove(characterId, out var signup))
			{
				logger.LogWarning("ReadyGroupFinder from {CharacterId:X8} with no pending signup; ignored.", characterId);
				return;
			}

			if (!ready || isTimeout)
			{
				// No capture declines a ready check; dropping the signup silently is the conservative reading of the
				// client re-queueing from scratch afterwards.
				logger.LogInformation("GroupFinder: {CharacterId:X8} did not confirm the ready check (timeout: {Timeout}); signup dropped.", characterId, isTimeout);
				return;
			}

			// A solo confirm resolves the whole check: one ready, none declined, one expected (the captured tally).
			await Send(client, CSPlayerInterfaceMethods.DispatchGroupFinderReadyCheckStatus, GroupFinderReadyCheckStatusCodec.Build(1, 0, 1));

			// The finder clears the LFT flag as the team forms.
			await Send(client, CSPlayerInterfaceMethods.DispatchCharacterLookingForTeamStatus,
				LookingForTeamStatusCodec.Build(new CharacterLookingForTeamStatus(false, "")));

			// The private team (#512): retail's DispatchYouJoinedTeam here carries a ONE-member roster, and the
			// instance entered three seconds later names that team as its owner. A queuer already in a team takes
			// that team along instead.
			var teamId = await teams.FormGroupFinderTeam(characterId);

			await Send(client, CSPlayerInterfaceMethods.DispatchGroupFinderActivityStarted, []);

			var activityId = ActivityOf(signup);
			if (!ActivityDestinations.TryGetValue(activityId, out var destination))
			{
				// The flow completed but the ActivityId->playfield remap of RDB 1000627 is un-walked past the
				// captured pair; refusing to guess beats teleporting into an invented destination.
				logger.LogWarning("GroupFinder: activity {Activity} has no mapped destination (only the captured 38 -> 1092 is); team 0x{Team:X8} formed, no teleport.",
					activityId, teamId);

				return;
			}

			logger.LogInformation("GroupFinder: {CharacterId:X8} enters activity {Activity} - map {MapId} as team 0x{Team:X8}.", characterId, activityId, destination.MapId, teamId);
			await gameServerDispatcher.TeleportCharacter(characterId, destination.MapId, destination.Arrival);
		}

		public void HandleLeave(uint characterId)
		{
			// c2s LeaveGroupFinder: withdraw from the queue. Leaving the ENTERED instance is the zone-line path, not
			// this - the DispatchGroupFinderRemoved push there already rides the Authenticate flow.
			if (_pendingSignups.TryRemove(characterId, out _))
				logger.LogInformation("GroupFinder: {CharacterId:X8} left the queue.", characterId);
		}

		// The single checked activity of the captured flow. More than one set bit is a multi-select the corpus never
		// shows; the first id keeps the reply well-formed and the log names the rest.
		private uint ActivityOf(QueueGroupFinderRequest signup)
		{
			var ids = signup.ActivityIds.ToArray();
			if (ids.Length > 1)
				logger.LogWarning("GroupFinder: {Count} activities checked ({Ids}); only the first drives the flow.", ids.Length, string.Join(", ", ids));

			return ids.FirstOrDefault();
		}

		private static Task Send(INetworkClient client, CSPlayerInterfaceMethods opcode, byte[] body)
		{
			return TeamService.SendToClient(client, opcode, body);
		}
	}
}
