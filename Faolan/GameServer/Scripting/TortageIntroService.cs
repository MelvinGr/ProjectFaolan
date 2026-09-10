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
	// The scripted TORTAGE-INTRO narrative, pulled out of WorldBroadcaster + CreateFlowService (#353): the
	// Kalanthes create-cutscene walk-up and the Casilda beach-rescue sequence (drop the invisible chain-wall, the
	// captive/freed poses, the freed-check). Keeps this bespoke story logic in one place instead of scattered across
	// the generic broadcaster/create-flow. Reads quest state (`IWorldRepository`) and resolves live entities via
	// `WorldSimulation`; the wire sends reuse the same world-conversation framing as every rendered NPC move/pose.
	//
	// It depends only on the simulation + DB (constructed before WorldBroadcaster), so the broadcaster's interest
	// stream can call INTO it (Casilda's captive pose on stream-in) without a construction cycle - it never depends
	// back on the broadcaster.
	public sealed class TortageIntroService
	{
		// ---- Casilda beach rescue --------------------------------------------------------------------------------
		// Casilda's NPC template. Her captive/freed poses (below) are decoded byte-exact for HER alone, so anything that
		// sends them is gated to this id - the follow + beach-wall generalised (Escort goal / WorldObject.EffectQuestId),
		// but the POSE stays Casilda-specific until per-NPC pose data exists.
		public const uint CasildaTemplateId = 9411;

		// The ordinary BCC (behaviour-control) state, against her captive 80 - see SendCasildaPose.
		public const uint OrdinaryBccState = 1;

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
		private readonly IWorldRepository _database;
		private readonly ILogger _logger;
		private readonly WorldSimulation _sim;

		public TortageIntroService(IWorldRepository database, WorldSimulation sim, ILogger logger)
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
		public async Task SendCasildaPose(INetworkClient client, uint casildaHandle, bool captive)
		{
			// The whole record comes off BehaviorControlCenterCodec, sub-opcode included. It used to be built through the
			// create-burst helper, which slices the sub-op off for its own sender - so this path had to write the
			// 0x642CD3D6 word back on by hand, and a BCC record that does not start with it is dropped by the client.
			if (captive)
			{
				await WorldIir.Send(client, BehaviorControlCenterCodec.Build(BehaviorAnimations.Registration(casildaHandle, 5, 159428, 1, 24, 0)));
				_logger.LogInformation("#267: sent Casilda CAPTIVE pose (handle 0x{Handle:X8}) to {Player}.", casildaHandle, client.Session.Character.Name);
				return;
			}

			// ⚠️ FREEING HER TAKES TWO RECORDS, and sending only the first left her walking around still playing the
			// chained struggle - reported in-client (#827). A registration is per SLOT, and the struggle lives in slot
			// 5: writing her locomotion into slot 9 adds a behaviour, it does not replace the one that is looping.
			//
			// Retail's rescue in Hoxxling_..._login_gameplay is three consecutive records on her handle 0x00017ED0,
			// 4 frames apart, and the third is the one that stops the struggle:
			//
			//   idx 2104  slot 7  159428  + a large field4     an END stamp on the captive behaviour (see below)
			//   idx 2106  slot 9   42010  + bound tail 0x12    her locomotion - the only one we used to send
			//   idx 2110  slot 5  142007  field12 260          OVERWRITES the struggle registration in slot 5
			//
			// So slot 5 goes from 159428 (struggle) to 142007, which is what actually un-chains her.
			//
			// ⛔ 2104 is deliberately NOT replayed: its field4 is a large absolute value that reads as a timestamp, and
			// replaying one capture's clock is the kind of thing that looks right until it is not. She frees correctly
			// without it; if a residual artefact ever shows up at the rescue, that record is the first suspect.
			// ⭐ FIRST the BCC STATE, which is what actually lets her walk (#827). `CharacterStatId.CombatMode` (0x14) is
			// "BCC State" to the client and selects the behaviour tree; her type ships 80 (the captive tree, where 1,186
			// templates carry the ordinary 1), so until this lands her locomotion never runs - she was moved by the
			// server while playing a standing animation, which is what "sliding" was. Retail sends exactly this one-stat
			// update at the rescue, immediately before the three BCC records (capture idx 2103, 29 bytes).
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
			// One projection over the goal rows (#940). This used to load EVERY quest the character had ever held with
			// the full definition graph and walk their goals for one Escort - 4.3-4.9 s of every world entry for a
			// level-80, paid in the post-ready block where the client's spawn polls queue behind it, which is what the
			// "lag spike after the loading screen" on a teleport turned out to be.
			return _database.GetEscorteeTemplateId(characterId);
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
