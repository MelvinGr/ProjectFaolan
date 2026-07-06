using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
using Faolan.GameServer.World;

// ScryScript (the destiny cutscene step builder)

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer.CreateFlow
{
	// #234 reconstruct STEP 2 (docs/handoff-2026-06-22-create-flow-reconstruct.md). The post-creation world entry
	// is now the PROVEN burst-less world (live char) PLUS the destiny cutscene's Scry-script overlay - WITHOUT the
	// verbatim captured-`hoxxling` world content. The beach/Kalanthes cutscene is server-driven: the client fires
	// callbacks (ClientLoadingDone -> StartScript -> StartScript -> Move -> IntroDone) and we answer each with the
	// next Scry step so it advances to IntroDone and the client transitions into the (burst-less) world. The
	// cutscene's ACTORS stay invisible until the #185 scene-spawn lift, but the flow PROGRESSES and the created char
	// lands playable (instead of the verbatim path's wrong-char world -> disconnect -> hang). The Scry steps + their
	// framing are lifted verbatim from RetailCreateWorldEntryBurst (the golden); only the world bulk is dropped.
	public partial class CreateFlowService
	{
		private const float KalanthesWalkPathLength = 8.567047119140625f; // from -> target distance (interp timing)
		private static readonly Quaternion KalanthesIntroFacing = new Quaternion(0f, -0.743186891078949f, 0f, 0.6690838932991028f);

		// #234/#193 Kalanthes walk-up, CONSTRUCTED from typed MoveTask data (no replayed hex blob). Decoded from the
		// captured MoveTasks (96C46740) Blablabla_* f657 (intro) + f890 (walk): the intro is a mode-2 Rotate that SNAPS
		// Kalanthes to the wash-ashore spot facing the player; the walk is a ListMoveTask[ PathMoveTask(2 waypoints) +
		// WaitMoveTask ] that moves him UP to the player. Positions are absolute beach coords (byte-exact - the floats
		// round-trip to the captured bits, KalanthesCutsceneMoveTests). Built with the LIVE Kalanthes handle directly.
		private static readonly Vector3 KalanthesIntroPos = new Vector3(333.20001220703125f, 146.29666137695312f, 706.0260009765625f);
		private static readonly Vector3 KalanthesWalkTarget = new Vector3(324.7233581542969f, 145.43357849121094f, 705.1339721679688f);

		// One cutscene step per client callback, driving the beach/Kalanthes destiny sequence to IntroDone. This is a
		// call-and-RESPONSE: decoded from Blablabla_* (port 7020), retail's world connection runs
		//   C2S PlayResult                  -> S2C ThisIsKalanthes
		//   C2S StartScript int=7088774     -> S2C StartGroup 7088774  (then, ~2.7s later, WelcomeTutorial)
		//   C2S StartScript int=7124502     -> S2C StartGroup 7124502
		//   C2S Move                        -> S2C Kneel
		//   C2S IntroDone                   -> S2C StartFixed3d (+ StartGroup 7727158)
		// The crucial bit: the client REQUESTS each script group by id (StartScript carries the id in field 3) and
		// waits for the server to ECHO BACK a StartGroup with the SAME id before it actually runs the group. Group
		// 7088774 is Kalanthes's welcome-dialogue group, so without that echoed confirmation the speech never plays.
		// (The earlier counter-based version pushed StartGroup 7088774 prematurely on ClientLoadingDone and then
		// answered the client's StartScript 7088774 with WelcomeTutorial instead of the StartGroup echo -> no speech.)
		// `groupId` is the requested id from the StartScript callback (0 for the other lifecycle callbacks).
		internal async Task SendCreateCutsceneStep(INetworkClient client, string token, uint groupId)
		{
			var id = client.Session.Account.ClientInstance;
			switch (token)
			{
				case "PlayResult":
					// #234 cinematic actor-binding: ThisIsKalanthes's f6 = {0xC350 type, <Kalanthes ENTITY HANDLE>} -
					// it tells the cutscene which live entity is Kalanthes (the walk-up + speech actor). Decoded from
					// Blablabla_* f658 (f6Field2 = 0x2C1C = the live Kalanthes spawn handle, == the MoveTask handle).
					// Bind OUR live Kalanthes (streamed by EnsureZoneNpcs, within range) or fall back if unloaded.
					// Retail sends this in RESPONSE to PlayResult (AFTER the player commits to playing the cutscene) -
					// binding BEFORE the cinematic starts (on ClientLoadingDone) loses the actor when it initialises.
					await SendScry(client, ScryScript.BuildStep(id, "ThisIsKalanthes", f6Field1: 50000, f6Field2: LiveNpcHandle(client, "Kalanthes", 0x2c1c)));
					// Position Kalanthes next to the washed-ashore player (his cutscene start), so the walk-up reads
					// (his DB spawn is back by the rocks). The walk path itself follows on the Move callback.
					await SendKalanthesMoveTask(client, BuildKalanthesIntroMove(LiveNpcHandle(client, "Kalanthes", 0x2c1c)));
					break;

				case "ClientLoadingDone":
					// World finished loading. Retail's beach binds the actor on PlayResult (above), not here - no-op.
					break;

				case "StartScript":
					// Echo back the StartGroup the client just requested (same id) - the confirmation that actually
					// RUNS the group on the client. Retail sends exactly this in response to each StartScript.
					await SendScry(client, ScryScript.BuildStartScriptGroup(id, groupId));
					// 7088774 is Kalanthes's welcome-dialogue group; retail follows its StartGroup with the
					// WelcomeTutorial step (~2.7s later, no triggering callback) - bundle it here, callback-driven.
					if (groupId == 7088774)
						await SendScry(client, ScryScript.BuildStep(id, "WelcomeTutorial"));
					break;

				case "Move":
					// Kalanthes walks up to the player (f890 path), then kneels.
					await SendKalanthesMoveTask(client, BuildKalanthesWalkMove(LiveNpcHandle(client, "Kalanthes", 0x2c1c)));
					await SendScry(client, ScryScript.BuildStep(id, "Kneel"));
					break;

				case "IntroDone":
					await SendScry(client, ScryScript.BuildStep(id, "StartFixed3d"));
					await SendScry(client, ScryScript.BuildStartScriptGroup(id, 7727158));
					break;
			}
		}

		// The live wire handle of the in-zone NPC named `npcName` (the cinematic actor to bind), or `fallback` when
		// it isn't loaded. Used to re-address the cutscene's actor reference (ThisIsKalanthes f6) to OUR live entity.
		private uint LiveNpcHandle(INetworkClient client, string npcName, uint fallback)
		{
			var player = _simulation.PlayerWorldEntity(client);
			if (player == null)
				return fallback;

			var npc = _simulation.World.ZoneForEntity(player).Entities.FirstOrDefault(e => !e.IsPlayer && e.Name == npcName);
			return npc?.Handle ?? fallback;
		}

		// The post-creation world entry: handshake + burst-less live-char world + cutscene start.
		internal async Task SendCreateWorldWithCutscene(INetworkClient client)
		{
			var id = client.Session.Account.ClientInstance;

			// (a) The create-world handshake - the three records the verbatim scene burst opened with (the shared
			// Hello handshake at GameServerListener is skipped for isCreateWorldEntry, so send it here).
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameAgentType, 0x08), ConanEndpoints.EndpointBytes(ConanEndpoints.GameInterfaceType, id), GameInterfaceDispatchOpcodes.DispatchReportDimesionID)
				.WriteArray(0x00, 0x01, 0x31)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameAgentType, 0x08), ConanEndpoints.EndpointBytes(ConanEndpoints.GameInterfaceType, id), GameInterfaceDispatchOpcodes.DispatchReportServerID)
				.WriteArray(0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x03)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameAgentType, 0x08), ConanEndpoints.EndpointBytes(ConanEndpoints.GameInterfaceType, id), GameInterfaceDispatchOpcodes.DispatchAckAuthenticate)
				.WriteArray(0x00, 0x00, 0x00, 0x01)
				.SendAsync(client);

			// (b) The live-char world (constructed; same path existing-char logins use).
			await _sendBurstlessWorldEntry(client, client.Session.Character.MapId);

			// (c) Start the destiny cutscene (Scry scripts only - the begin marker, the scene-load character_creation
			// step, and the two opening script groups). The per-callback steps follow via SendCreateCutsceneStep.
			// EnergyIIR (0x40749B8A) seed on the avatar - constructed via CreateEditorRecordBuilder (byte-exact-locked
			// by CreateEditorRecordTests), was a verbatim blob. flag=1, value=0x36B0.
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameAgentType, 0x08), ConanEndpoints.EndpointBytes(ConanEndpoints.GameInterfaceType, id), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s.WriteUInt32(GameServer0X2000RespondsOpcodes.EnergyIIR).WriteArray(CreateEditorRecordBuilder.BuildEnergyIir(CreateEditorRecordBuilder.CharacterWireType, id, 0x01, 0x36B0)))
				.SendAsync(client);
			await SendScry(client, ScryScript.BuildStartScriptGroup(id, 48126));
			await SendScry(client,
				ScryScript.BuildStep(id,
					"<remoteformat token=\"playorskip(100)|onclientconnected(1)|character_creation Tortage Beach (server-side)\" category=\"50023\" key=\"*&#36;!0mcl&#36;Z!5Ii0UG!TV?\" knubot=\"0\"  ></remoteformat>",
					6,
					resources:
					"1010003:3147889;1010003:3147890;1010003:3147891;1010003:3147892;1010003:3147893;1010003:3147894;1010003:137283;1010003:202270;1010003:210528;1010003:205370;1010003:3147896;1010003:3147895;1010003:3147897;1010003:3147898;1010003:3147899;1010003:3147900;1010003:208235;1010003:3203880;1010003:3203879;1010003:3203881;1010003:3203876;1010003:3203877;1010003:3203878",
					f6Field1: 4617355,
					f6Field2: 7004418));
			await SendScry(client, ScryScript.BuildStartScriptGroup(id, 385910));
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentProjectType, 0x08), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, id), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x01, 0x7E, 0x03, 0x00, 0x00, 0x00, 0x00)
				.SendAsync(client);
		}

		// Sends a constructed Kalanthes MoveTask on the world conversation (WorldFraming + IirCarrier - the same framing
		// every rendered NPC move goes out on).
		private Task SendKalanthesMoveTask(INetworkClient client, byte[] body)
		{
			return new PacketStream().WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob).WriteArrayUInt32Length(s => s.WriteArray(body)).SendAsync(client);
		}

		// The intro TRANSFORM: a stationary (from==to) mode-2 MoveTask placing Kalanthes at his cutscene start.
		internal static byte[] BuildKalanthesIntroMove(uint handle)
		{
			return MoveTaskCodec.BuildBody(handle, KalanthesIntroPos, KalanthesIntroPos, KalanthesIntroFacing);
		}

		// The WALK: ListMoveTask[ PathMoveTask(from -> target) + WaitMoveTask ], the exact captured composite shape,
		// built from the typed model so it round-trips byte-exact while the positions stay explicit data.
		internal static byte[] BuildKalanthesWalkMove(uint handle)
		{
			var path = new MoveTaskNode
			{
				Mode = 1,
				HeaderFlags = 1,
				PathFlags = 0,
				Waypoints =
				[
					B(KalanthesIntroPos.X), B(KalanthesIntroPos.Y), B(KalanthesIntroPos.Z),
					B(KalanthesWalkTarget.X), B(KalanthesWalkTarget.Y), B(KalanthesWalkTarget.Z)
				],
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

		// A ScryScriptIIR step on the GameAgent->GameInterface conversation (the framing the verbatim burst uses).
		private static Task SendScry(INetworkClient client, byte[] scry)
		{
			var id = client.Session.Account.ClientInstance;
			return new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameAgentType, 0x08), ConanEndpoints.EndpointBytes(ConanEndpoints.GameInterfaceType, id), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s.WriteArray(scry))
				.SendAsync(client);
		}
	}
}
