using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.World;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Quest
{
	// The scripted Tortage-intro narrative handling, pulled out of WorldBroadcaster + CreateFlowService (#353): the
	// Kalanthes create-cutscene walk-up and the Casilda beach-rescue sequence (drop the invisible chain-wall, the
	// captive/freed poses, the freed-check). Keeps this bespoke story logic in one place instead of scattered across
	// the generic broadcaster/create-flow. Reads quest state (`IDatabaseRepository`) and resolves live entities via
	// `WorldSimulation`; the wire sends reuse the same world-conversation framing as every rendered NPC move/pose.
	//
	// It depends only on the simulation + DB (constructed before WorldBroadcaster), so the broadcaster's interest
	// stream can call INTO it (Casilda's captive pose on stream-in) without a construction cycle - it never depends
	// back on the broadcaster.
	public sealed class QuestHandlerService
	{
		// ---- Casilda beach rescue --------------------------------------------------------------------------------
		// Casilda's NPC template. Her captive/freed poses (below) are decoded byte-exact for HER alone, so anything that
		// sends them is gated to this id - the follow + beach-wall generalised (Escort goal / WorldObject.EffectQuestId),
		// but the POSE stays Casilda-specific until per-NPC pose data exists.
		public const uint CasildaTemplateId = 9411;

		// ---- Kalanthes intro cutscene (create flow) --------------------------------------------------------------
		// #234/#193 Kalanthes walk-up, CONSTRUCTED from typed MoveTask data (no replayed hex blob). Decoded from the
		// captured MoveTasks (96C46740) Blablabla_* f657 (intro) + f890 (walk): the intro is a mode-2 Rotate that SNAPS
		// Kalanthes to the wash-ashore spot facing the player; the walk is a ListMoveTask[ PathMoveTask(2 waypoints) +
		// WaitMoveTask ] that moves him UP to the player. Positions are absolute beach coords (byte-exact - the floats
		// round-trip to the captured bits, KalanthesCutsceneMoveTests). Built with the LIVE Kalanthes handle directly.
		private const float KalanthesWalkPathLength = 8.567047119140625f; // from -> target distance (interp timing)
		private static readonly Quaternion KalanthesIntroFacing = new Quaternion(0f, -0.743186891078949f, 0f, 0.6690838932991028f);
		private static readonly Vector3 KalanthesIntroPos = new Vector3(333.20001220703125f, 146.29666137695312f, 706.0260009765625f);
		private static readonly Vector3 KalanthesWalkTarget = new Vector3(324.7233581542969f, 145.43357849121094f, 705.1339721679688f);
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;
		private readonly WorldSimulation _sim;

		public QuestHandlerService(IDatabaseRepository database, WorldSimulation sim, ILogger logger)
		{
			_database = database;
			_sim = sim;
			_logger = logger;
		}

		// #262/#267: clear CASILDA's captive (chained) struggle pose so she stands/walks normally as she follows - the
		// live counterpart to the captive pose the interest stream sends while she's a prisoner. Deliberately NOT
		// generic: unlike the follow (Escort goal) and the wall (WorldObject.EffectQuestId), the pose BCC behaviorIds
		// (159428 captive / 42010 freed) are Casilda's alone, so this is GATED to her template and no-ops for any other
		// escortee (they'd need their own decoded poses). Called on the escort start + world entry.
		public async Task ClearCasildaCaptivePose(INetworkClient client)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || await EscorteeTemplateId(client.Session.Character.Id) != CasildaTemplateId)
				return;

			var casilda = _sim.World.ZoneForEntity(player).Entities.FirstOrDefault(e => e is { IsPlayer: false, NpcTemplateId: CasildaTemplateId });
			if (casilda != null)
				await SendCasildaPose(client, casilda.Handle, false);
		}

		// Whether the escortee has been freed - data-driven off the ESCORT goal (no hard-coded id/name): the player has a
		// quest with an Escort goal that is Active or Completed. The escort stage is granted the instant she's freed
		// (the talk-to-free completes -> escort superseded), so "has an escort quest" == "freed" (#262). The beach gate
		// then stays down.
		public async Task<bool> IsCasildaFreed(uint characterId)
		{
			return await EscorteeTemplateId(characterId) != 0;
		}

		// #267/#266: Casilda's captive (chained) vs freed pose, as a BehaviorControlCenterIIR. Both decoded byte-exact
		// from the Hoxxling beach gameplay capture's BCC (0x642CD3D6) records targeting her handle 0x00017ED0:
		//  - CAPTIVE = the prisoner struggle the destiny scene loops (slot 5, behaviorId 159428, field5 24, no tail;
		//    retail alternates 159428/159427 each phase but a single registration loops it).
		//  - FREED = her slot-9 locomotion behavior 42010 (field5 24) WITH the 20-byte bound tail (boundA 0x12), the
		//    record retail sends at the rescue (capture idx 2106) so she stops struggling and walks when following.
		public Task SendCasildaPose(INetworkClient client, uint casildaHandle, bool captive)
		{
			// The whole record comes off BehaviorControlCenterCodec, sub-opcode included. It used to be built through the
			// create-burst helper, which slices the sub-op off for its own sender - so this path had to write the
			// 0x642CD3D6 word back on by hand, and a BCC record that does not start with it is dropped by the client.
			var body = BehaviorControlCenterCodec.Build(captive
				? BehaviorAnimations.Registration(casildaHandle, 5, 159428, 1, 24, 0)
				: BehaviorAnimations.Registration(casildaHandle, 9, 42010, 7, 24, 0, boundA: 0x12, boundB: 0));
			_logger.LogInformation("#267: sent Casilda {Pose} pose (handle 0x{Handle:X8}) to {Player}.", captive
				? "CAPTIVE"
				: "freed", casildaHandle, client.Session.Character.Name);
			return WorldIir.Send(client, body);
		}

		// The template id of the escortee the player is (or was) escorting - the Escort goal's TrailingRefId on any
		// Active/Completed escort quest, or 0 if none. Used to resolve the escortee live by ID rather than by name.
		private async Task<uint> EscorteeTemplateId(uint characterId)
		{
			foreach (var quest in await _database.GetCharacterQuests(characterId))
			{
				if (quest.State != QuestState.Active && quest.State != QuestState.Completed)
					continue;

				var def = await _database.GetQuestDefinition(quest.QuestId);
				if (def?.Goals.FirstOrDefault(g => g.GoalType == QuestGoalType.Escort) is { TrailingRefId: not 0 } escort)
					return escort.TrailingRefId;
			}

			return 0;
		}

		// The intro TRANSFORM: a stationary (from==to) mode-2 MoveTask placing Kalanthes at his cutscene start.
		public static byte[] BuildKalanthesIntroMove(uint handle)
		{
			return MoveTaskCodec.Build(new MoveTaskIIR(handle, KalanthesIntroPos, MoveTaskNodes.Rotate(KalanthesIntroPos, KalanthesIntroFacing)));
		}

		// The WALK: ListMoveTask[ PathMoveTask(from -> target) + WaitMoveTask ] - which is exactly the canonical
		// single-segment PATROL shape, so it is `MoveTaskNodes.Patrol` with the cutscene's two waypoints rather than a
		// second hand-built copy of that tree (#530 wave 7 - the node fields it used to spell out here, down to the
		// trailer's anchor triple, were the factory's). The captured path LENGTH is passed rather than recomputed: it
		// differs from a recomputed float by ~1 ULP. Positions stay HERE because they are quest content.
		public static byte[] BuildKalanthesWalkMove(uint handle)
		{
			return MoveTaskCodec.Build(new MoveTaskIIR(handle, KalanthesIntroPos, MoveTaskNodes.Patrol([KalanthesIntroPos, KalanthesWalkTarget], KalanthesWalkPathLength, KalanthesIntroPos)));
		}

		// Sends a constructed Kalanthes MoveTask on the world conversation (WorldSenderEndpoint + IirCarrier - the same framing
		// every rendered NPC move goes out on).
		public static Task SendKalanthesMoveTask(INetworkClient client, byte[] body)
		{
			return WorldIir.Send(client, body);
		}

	}
}
