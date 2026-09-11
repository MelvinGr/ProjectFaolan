using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.World;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Scripting
{
	// The scripted Tortage-intro narrative: the Kalanthes create-cutscene walk-up and the Casilda beach-rescue sequence
	// (dropping the invisible chain-wall, the captive and freed poses, the freed check). Bespoke story logic lives here
	// rather than in the generic broadcaster and create flow. It reads quest state (`IWorldRepository`) and resolves
	// live entities via `WorldSimulation`; the wire sends reuse the same world-conversation framing as every rendered
	// NPC move or pose.
	//
	// It depends only on the simulation and the database, both constructed before WorldBroadcaster, so the
	// broadcaster's interest stream can call into it (Casilda's captive pose on stream-in) without a construction
	// cycle. It must never depend back on the broadcaster.
	public sealed class TortageIntroService
	{
		// ---- Casilda beach rescue --------------------------------------------------------------------------------
		// Casilda's NPC template. Her captive and freed poses below are decoded byte-exact for her alone, so anything
		// that sends them is gated to this id. The follow and the beach wall are generalised (Escort goal,
		// WorldObject.EffectQuestId); the pose stays Casilda-specific until per-NPC pose data exists.
		public const uint CasildaTemplateId = 9411;

		// The ordinary BCC (behaviour-control) state, against her captive 80 - see SendCasildaPose.
		public const uint OrdinaryBccState = 1;

		// ---- Kalanthes intro cutscene (create flow) --------------------------------------------------------------
		// The Kalanthes walk-up, constructed from typed MoveTask data rather than a replayed hex blob. Decoded from the
		// captured MoveTasks (96C46740) Blablabla_* f657 (intro) and f890 (walk): the intro is a mode-2 Rotate that
		// snaps Kalanthes to the wash-ashore spot facing the player; the walk is a ListMoveTask[ PathMoveTask(2
		// waypoints) + WaitMoveTask ] that moves him up to the player. Positions are absolute beach coordinates and are
		// byte-exact - the floats round-trip to the captured bits (KalanthesCutsceneMoveTests). Built with the live
		// Kalanthes handle.
		private const float KalanthesWalkPathLength = 8.567047119140625f; // from -> target distance (interp timing)
		private static readonly Quaternion KalanthesIntroFacing = new Quaternion(0f, -0.743186891078949f, 0f, 0.6690838932991028f);
		private static readonly Vector3 KalanthesIntroPos = new Vector3(333.20001220703125f, 146.29666137695312f, 706.0260009765625f);
		private static readonly Vector3 KalanthesWalkTarget = new Vector3(324.7233581542969f, 145.43357849121094f, 705.1339721679688f);
		private readonly IWorldRepository _database;
		private readonly ILogger _logger;
		private readonly WorldSimulation _sim;

		public TortageIntroService(IWorldRepository database, WorldSimulation sim, ILogger logger)
		{
			_database = database;
			_sim = sim;
			_logger = logger;
		}

		// Clears Casilda's captive (chained) struggle pose so she stands and walks normally as she follows - the live
		// counterpart to the captive pose the interest stream sends while she is a prisoner. Deliberately not generic:
		// unlike the follow (Escort goal) and the wall (WorldObject.EffectQuestId), the pose BCC behaviour ids (159428
		// captive, 42010 freed) are Casilda's alone, so this is gated to her template and is a no-op for any other
		// escortee, which would need its own decoded poses. Called on the escort start and at world entry.
		public async Task ClearCasildaCaptivePose(INetworkClient client)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || await EscorteeTemplateId(client.Session.Character.Id) != CasildaTemplateId)
				return;

			var casilda = _sim.World.ZoneForEntity(player).Entities.FirstOrDefault(e => e is { IsPlayer: false, NpcTemplateId: CasildaTemplateId });
			if (casilda != null)
				await SendCasildaPose(client, casilda.Handle, false);
		}

		// Whether the escortee has been freed - data-driven off the Escort goal, with no hard-coded id or name: the
		// player has a quest with an Escort goal that is Active or Completed. The escort stage is granted the instant
		// she is freed (the talk-to-free completes and the escort supersedes it), so "has an escort quest" means
		// "freed", and the beach gate then stays down.
		public async Task<bool> IsCasildaFreed(uint characterId)
		{
			return await EscorteeTemplateId(characterId) != 0;
		}

		// Casilda's captive (chained) versus freed pose, as a BehaviorControlCenterIIR. Both decoded byte-exact from
		// the Hoxxling beach gameplay capture's BCC (0x642CD3D6) records targeting her handle 0x00017ED0:
		//  - Captive: the prisoner struggle the destiny scene loops (slot 5, behaviorId 159428, field5 24, no tail;
		//    retail alternates 159428/159427 each phase but a single registration loops it).
		//  - Freed: her slot-9 locomotion behaviour 42010 (field5 24) with the 20-byte bound tail (boundA 0x12), the
		//    record retail sends at the rescue (capture idx 2106) so she stops struggling and walks when following.
		public async Task SendCasildaPose(INetworkClient client, uint casildaHandle, bool captive)
		{
			// The whole record comes off BehaviorControlCenterCodec, sub-opcode included: a BCC record that does not
			// start with the 0x642CD3D6 word is dropped by the client.
			if (captive)
			{
				await WorldIir.Send(client, BehaviorControlCenterCodec.Build(BehaviorAnimations.Registration(casildaHandle, 5, 159428, 1, 24, 0)));
				_logger.LogInformation("#267: sent Casilda CAPTIVE pose (handle 0x{Handle:X8}) to {Player}.", casildaHandle, client.Session.Character.Name);
				return;
			}

			// Freeing her takes two records: with only the first she walks around still playing the chained struggle.
			// A registration is per slot, and the struggle lives in slot 5, so writing her locomotion into slot 9 adds
			// a behaviour rather than replacing the one that is looping.
			//
			// Retail's rescue in Hoxxling_..._login_gameplay is three consecutive records on her handle 0x00017ED0,
			// 4 frames apart, and the third is the one that stops the struggle:
			//
			//   idx 2104  slot 7  159428  + a large field4     an end stamp on the captive behaviour (see below)
			//   idx 2106  slot 9   42010  + bound tail 0x12    her locomotion
			//   idx 2110  slot 5  142007  field12 260          overwrites the struggle registration in slot 5
			//
			// So slot 5 goes from 159428 (struggle) to 142007, which is what actually un-chains her.
			//
			// 2104 is deliberately not replayed: its field4 is a large absolute value that reads as a timestamp, and
			// replaying one capture's clock is the kind of thing that looks right until it is not. She frees correctly
			// without it; if a residual artefact ever shows up at the rescue, that record is the first suspect.
			//
			// The BCC state comes first, because it is what lets her walk at all. `CharacterStatId.CombatMode` (0x14)
			// is "BCC State" to the client and selects the behaviour tree; her type ships 80, the captive tree, where
			// 1,186 templates carry the ordinary 1. Until this lands her locomotion never runs and the server moves her
			// while she plays a standing animation, which renders as sliding. Retail sends exactly this one-stat update
			// at the rescue, immediately before the three BCC records (capture idx 2103, 29 bytes).
			await WorldIir.Send(client, StatUpdateCodec.Build(casildaHandle, new CharacterStatBlock().SetRaw(CharacterStatId.CombatMode, OrdinaryBccState)));

			await WorldIir.Send(client, BehaviorControlCenterCodec.Build(BehaviorAnimations.CompanionLocomotion(casildaHandle, 9, 42010, 7, 24, 0)));
			await WorldIir.Send(client, BehaviorControlCenterCodec.Build(BehaviorAnimations.Registration(casildaHandle, 5, 142007, 8, 24, 0, field12: 260)));
			_logger.LogInformation("#267: sent Casilda freed pose (handle 0x{Handle:X8}) to {Player} - locomotion 42010 (slot 9) + slot-5 behaviour 142007 replacing the struggle.",
				casildaHandle, client.Session.Character.Name);
		}

		// The template id of the escortee the player is (or was) escorting - the Escort goal's TrailingRefId on any
		// Active/Completed escort quest, or 0 if none. Used to resolve the escortee live by ID rather than by name.
		private Task<uint> EscorteeTemplateId(uint characterId)
		{
			// One projection over the goal rows. Loading every quest the character has ever held with its full
			// definition graph, to walk the goals for one Escort, costs 4.3-4.9 s of every world entry for a level-80 -
			// paid in the post-ready block where the client's spawn polls queue behind it, so the player sees it as a
			// lag spike after the loading screen.
			return _database.GetEscorteeTemplateId(characterId);
		}

		// The intro transform: a stationary (from == to) mode-2 MoveTask placing Kalanthes at his cutscene start.
		public static byte[] BuildKalanthesIntroMove(uint handle)
		{
			return MoveTaskCodec.Build(new MoveTaskIIR(handle, KalanthesIntroPos, MoveTaskNodes.Rotate(KalanthesIntroPos, KalanthesIntroFacing)));
		}

		// The walk: ListMoveTask[ PathMoveTask(from -> target) + WaitMoveTask ], which is the canonical single-segment
		// patrol shape, so it uses `MoveTaskNodes.Patrol` with the cutscene's two waypoints rather than a second
		// hand-built copy of that tree. The captured path length is passed rather than recomputed, because it differs
		// from a recomputed float by about 1 ULP. The positions stay here because they are quest content.
		public static byte[] BuildKalanthesWalkMove(uint handle)
		{
			return MoveTaskCodec.Build(new MoveTaskIIR(handle, KalanthesIntroPos, MoveTaskNodes.Patrol([KalanthesIntroPos, KalanthesWalkTarget], KalanthesWalkPathLength, KalanthesIntroPos)));
		}

		// Sends a constructed Kalanthes MoveTask on the world conversation (WorldSenderEndpoint + IirCarrier, the same
		// framing every rendered NPC move goes out on).
		public static Task SendKalanthesMoveTask(INetworkClient client, byte[] body)
		{
			return WorldIir.Send(client, body);
		}

	}
}
