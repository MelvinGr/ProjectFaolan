using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Packets;
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
		private readonly IDatabaseRepository _database;
		private readonly WorldSimulation _sim;
		private readonly ILogger _logger;

		public QuestHandlerService(IDatabaseRepository database, WorldSimulation sim, ILogger logger)
		{
			_database = database;
			_sim = sim;
			_logger = logger;
		}

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

		// The intro TRANSFORM: a stationary (from==to) mode-2 MoveTask placing Kalanthes at his cutscene start.
		public static byte[] BuildKalanthesIntroMove(uint handle)
		{
			return MoveTaskCodec.BuildBody(handle, KalanthesIntroPos, KalanthesIntroPos, KalanthesIntroFacing);
		}

		// The WALK: ListMoveTask[ PathMoveTask(from -> target) + WaitMoveTask ], the exact captured composite shape,
		// built from the typed model so it round-trips byte-exact while the positions stay explicit data.
		public static byte[] BuildKalanthesWalkMove(uint handle)
		{
			var path = new MoveTaskNode
			{
				Mode = 1,
				HeaderFlags = 1,
				PathFlags = 0,
				Waypoints = [B(KalanthesIntroPos.X), B(KalanthesIntroPos.Y), B(KalanthesIntroPos.Z), B(KalanthesWalkTarget.X), B(KalanthesWalkTarget.Y), B(KalanthesWalkTarget.Z)],
				TrailerByte = 0,
				TrailerWords = [B(KalanthesWalkPathLength), 0, 0, 0, B(KalanthesIntroPos.X), B(KalanthesIntroPos.Y), B(KalanthesIntroPos.Z)]
			};
			var wait = new MoveTaskNode
			{
				Mode = 0,
				HeaderFlags = 1
			};
			return new MoveTaskComposite
			{
				Handle = handle,
				FromX = B(KalanthesIntroPos.X),
				FromY = B(KalanthesIntroPos.Y),
				FromZ = B(KalanthesIntroPos.Z),
				Task = new MoveTaskNode
				{
					Mode = 4,
					HeaderFlags = 1,
					ListMin = 0,
					Children = [path, wait]
				}
			}.Encode();

			static uint B(float f)
			{
				return BitConverter.SingleToUInt32Bits(f);
			}
		}

		// Sends a constructed Kalanthes MoveTask on the world conversation (WorldFraming + IirCarrier - the same framing
		// every rendered NPC move goes out on).
		public static Task SendKalanthesMoveTask(INetworkClient client, byte[] body) => SendWorldIir(client, body);

		// ---- Casilda beach rescue --------------------------------------------------------------------------------
		// #267: drop the beach "invisible wall". The wall is the Casilda chain decor's COLLISION (the
		// `004709_Global__decor_chain_hangingwoman` SimpleDynel sent at world entry; its mesh doesn't render for us, so
		// it reads as an invisible barrier). Retail flips it off with a SimpleDynel visual-change (state 5) to that dynel
		// the instant Casilda is freed on the beach (Hoxxling gameplay idx 2053). Looked up by the chain world-object's
		// identity so the handle matches what the client was sent at entry (its CapturedHandle). No-op off-beach / if the
		// map has no such chain.
		public async Task DropBeachWall(INetworkClient client)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return;

			var chain = (await _database.GetWorldObjects(player.MapId)).FirstOrDefault(o =>
				o.SubOpcode == SimpleDynelMessageCodec.Opcode && o.Name != null && o.Name.Contains("decor_chain_hangingwoman"));
			if (chain == null)
				return;

			await SendWorldIir(client, SimpleDynelMessageCodec.BuildVisualChange(chain.CapturedHandle, 0x05));
			_logger.LogInformation("#267: dropped beach chain-wall collision (handle 0x{Handle:X8}) for {Player}.", chain.CapturedHandle, player.Name);

			// She's freed now: clear the captive (chained) struggle pose so she stands/walks normally as she follows
			// (the live counterpart to the captive pose the interest stream sends while she's a prisoner).
			var casilda = _sim.World.ZoneForEntity(player).Entities.FirstOrDefault(e => !e.IsPlayer && e.Name == "Casilda");
			if (casilda != null)
				await SendCasildaPose(client, casilda.Handle, false);
		}

		// #267: at world entry, drop the wall up-front for a character who has ALREADY freed Casilda (the talk stage is
		// completed), so a returning player isn't blocked by the chain collision the world-entry burst re-sends. No-op
		// off the beach or before she's freed.
		public async Task SendBeachGateOnEntry(INetworkClient client)
		{
			var character = client.Session?.Character;
			if (character == null || character.MapId != Map.TortageBeachId)
				return;

			if (await IsCasildaFreed(character.Id))
				await DropBeachWall(client);
		}

		// Whether the character has freed Casilda - data-driven (no hard-coded quest ids): any COMPLETED quest whose
		// first goal is a TalkToNpc goal targeting Casilda (the freeing stage of the intro chain, #261).
		public async Task<bool> IsCasildaFreed(uint characterId)
		{
			foreach (var quest in await _database.GetCharacterQuests(characterId))
			{
				if (quest.State != QuestState.Completed)
					continue;

				var def = await _database.GetQuestDefinition(quest.QuestId);
				var goal = def?.Goals.OrderBy(g => g.Sequence).FirstOrDefault();
				if (goal?.GoalType == QuestGoalType.TalkToNpc && string.Equals(goal.TargetName, "Casilda", StringComparison.OrdinalIgnoreCase))
					return true;
			}

			return false;
		}

		// #267/#266: Casilda's captive (chained) vs freed pose, as a BehaviorControlCenterIIR. Both decoded byte-exact
		// from the Hoxxling beach gameplay capture's BCC (0x642CD3D6) records targeting her handle 0x00017ED0:
		//  - CAPTIVE = the prisoner struggle the destiny scene loops (slot 5, behaviorId 159428, field5 24, no tail;
		//    retail alternates 159428/159427 each phase but a single registration loops it).
		//  - FREED = her slot-9 locomotion behavior 42010 (field5 24) WITH the 20-byte bound tail (boundA 0x12), the
		//    record retail sends at the rescue (capture idx 2106) so she stops struggling and walks when following.
		public Task SendCasildaPose(INetworkClient client, uint casildaHandle, bool captive)
		{
			// BuildBehaviorControlCenterIir returns the record BODY ([type][handle][form][slot][protobuf]) WITHOUT the
			// 0x642CD3D6 sub-opcode (the create-burst Iir helper prepends it), so prepend it here - SendWorldIir only adds
			// the carrier + length, not the sub-op, and the client drops a BCC record that doesn't start with it.
			var record = captive
				? CreateEditorRecordBuilder.BuildBehaviorControlCenterIir(casildaHandle, 5, 159428, 1, 24, 0)
				: CreateEditorRecordBuilder.BuildBehaviorControlCenterIir(casildaHandle, 9, 42010, 7, 24, 0, boundA: 0x12, boundB: 0);
			var body = new FaolanStream().WriteUInt32(GameServer0X2000Opcodes.BehaviorControlCenterIIR).WriteArray(record).ToArray();
			_logger.LogInformation("#267: sent Casilda {Pose} pose (handle 0x{Handle:X8}) to {Player}.", captive
				? "CAPTIVE"
				: "freed", casildaHandle, client.Session?.Character?.Name);
			return SendWorldIir(client, body);
		}

		// The world-conversation IIR send (WorldFraming + IirCarrier) shared by the scripted sends above - the same
		// framing WorldBroadcaster uses for every rendered move/pose/visual-change.
		private static Task SendWorldIir(INetworkClient recipient, byte[] body)
		{
			return new PacketStream()
				.WriteHeader(WorldFraming.Sender, recipient.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s.WriteArray(body))
				.SendAsync(recipient);
		}
	}
}
