using System;
using System.Buffers.Binary;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
using Faolan.GameServer.World; // ScryScript (the destiny cutscene step builder)

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	// #234 reconstruct STEP 2 (docs/handoff-2026-06-22-create-flow-reconstruct.md). The post-creation world entry
	// is now the PROVEN burst-less world (live char) PLUS the destiny cutscene's Scry-script overlay - WITHOUT the
	// verbatim captured-`hoxxling` world content. The beach/Kalanthes cutscene is server-driven: the client fires
	// callbacks (ClientLoadingDone -> StartScript -> StartScript -> Move -> IntroDone) and we answer each with the
	// next Scry step so it advances to IntroDone and the client transitions into the (burst-less) world. The
	// cutscene's ACTORS stay invisible until the #185 scene-spawn lift, but the flow PROGRESSES and the created char
	// lands playable (instead of the verbatim path's wrong-char world -> disconnect -> hang). The Scry steps + their
	// framing are lifted verbatim from RetailCreateWorldEntryBurst (the golden); only the world bulk is dropped.
	public partial class GameServerListener
	{
		// The post-creation world entry: handshake + burst-less live-char world + cutscene start.
		private async Task SendCreateWorldWithCutscene(INetworkClient client)
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
			await SendBurstlessWorldEntry(client, client.Session.Character.MapId);

			// (c) Start the destiny cutscene (Scry scripts only - the begin marker, the scene-load character_creation
			// step, and the two opening script groups). The per-callback steps follow via SendCreateCutsceneStep.
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameAgentType, 0x08), ConanEndpoints.EndpointBytes(ConanEndpoints.GameInterfaceType, id), GameServerCarrierOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0x00, 0x11, 0x40, 0x74, 0x9B, 0x8A, 0x00, 0x00, 0xC3, 0x50)
				.WriteUInt32(id)
				.WriteArray(0x01, 0x00, 0x00, 0x36, 0xB0)
				.SendAsync(client);
			await SendScry(client, ScryScript.BuildStartScriptGroup(id, 48126));
			await SendScry(client, ScryScript.BuildStep(id, "<remoteformat token=\"playorskip(100)|onclientconnected(1)|character_creation Tortage Beach (server-side)\" category=\"50023\" key=\"*&#36;!0mcl&#36;Z!5Ii0UG!TV?\" knubot=\"0\"  ></remoteformat>", mode: 6, resources: "1010003:3147889;1010003:3147890;1010003:3147891;1010003:3147892;1010003:3147893;1010003:3147894;1010003:137283;1010003:202270;1010003:210528;1010003:205370;1010003:3147896;1010003:3147895;1010003:3147897;1010003:3147898;1010003:3147899;1010003:3147900;1010003:208235;1010003:3203880;1010003:3203879;1010003:3203881;1010003:3203876;1010003:3203877;1010003:3203878", f6Field1: 4617355, f6Field2: 7004418));
			await SendScry(client, ScryScript.BuildStartScriptGroup(id, 385910));
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentProjectType, 0x08), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, id), GameServerCarrierOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x01, 0x7E, 0x03, 0x00, 0x00, 0x00, 0x00)
				.SendAsync(client);
		}

		// One cutscene step per client callback (ClientLoadingDone -> StartScript -> StartScript -> Move -> IntroDone),
		// driving the beach/Kalanthes destiny sequence to IntroDone. Scry scripts only (no world content); the steps +
		// args are the ones the verbatim chunks (SendNextCreateWorldChunk cases 0-4) carry.
		internal async Task SendCreateCutsceneStep(INetworkClient client)
		{
			var id = client.Session.Account.ClientInstance;
			switch (client.Session.CreateWorldChunkIndex++)
			{
				case 0:
					// #234 cinematic actor-binding: ThisIsKalanthes's f6 = {0xC350 type, <Kalanthes ENTITY HANDLE>} -
					// it tells the cutscene which live entity is Kalanthes (the walk-up actor). Decoded from
					// Blablabla_* f658 (f6Field2 = 0x2C1C, a world-entry spawn handle). The captured 97795 is the
					// hoxxling session's handle - bind OUR live Kalanthes (streamed by EnsureZoneNpcs, within range)
					// or fall back to the captured value if he isn't loaded.
					await SendScry(client, ScryScript.BuildStep(id, "ThisIsKalanthes", mode: 2, f6Field1: 50000, f6Field2: LiveNpcHandle(client, "Kalanthes", 97795)));
					// Position Kalanthes next to the washed-ashore player (his cutscene start), so the walk-up reads
					// (his DB spawn is back by the rocks). The walk path itself follows on the Move callback (case 3).
					await SendKalanthesMoveTask(client, KalanthesIntroTransform);
					// Retail loads WelcomeTutorial's script group HERE (callback 0, with ThisIsKalanthes - capture
					// f665), then plays the dialogue step on the next callback. Bundling them (group+step together)
					// fired the step before its group was set up -> no dialogue.
					await SendScry(client, ScryScript.BuildStartScriptGroup(id, 7088774));
					break;
				case 1:
					await SendScry(client, ScryScript.BuildStep(id, "WelcomeTutorial", mode: 2));
					break;
				case 2:
					await SendScry(client, ScryScript.BuildStartScriptGroup(id, 7124502));
					break;
				case 3:
					// The Move callback: Kalanthes walks up to the player (f890 path), then kneels.
					await SendKalanthesMoveTask(client, KalanthesWalkPath);
					await SendScry(client, ScryScript.BuildStep(id, "Kneel", mode: 2));
					break;
				case 4:
					await SendScry(client, ScryScript.BuildStep(id, "StartFixed3d", mode: 2));
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

		// #234 Kalanthes walk-up: the two captured MoveTasks (96C46740) from Blablabla_* f657 + f890, stripped of
		// their leading [u32 len] (WriteArrayUInt32Length re-adds it). f657 is the intro TRANSFORM - it snaps
		// Kalanthes to (333.2,146.3,706.0), right next to the washed-ashore player; f890 is the WALK PATH - he
		// moves to (324.7,145.4,705.1), up to the player. Body layout: [96C46740][0000C350][handle u32 @8]...; the
		// captured handle 0x2C1C is re-addressed to OUR live Kalanthes (same replay-a-captured-MoveTask mechanism
		// the NPC patrol path uses). Positions are absolute beach coords keyed to the same wash-ashore spot, so they
		// transfer verbatim.
		private static readonly byte[] KalanthesIntroTransform = Convert.FromHexString(
			"96c467400000c35000002c1c000043a6999a43124bf2443081aa0243a6999a43124bf2443081aa00000000bf3e417f000000003f2b49150000");
		private static readonly byte[] KalanthesWalkPath = Convert.FromHexString(
			"96c467400000c35000002c1c000043a6999a43124bf2443081aa040100020101000000000243a6999a43124bf2443081aa43a25c9743116eff4430489300410912a000000000000000000000000043a6999a43124bf2443081aa0001");

		// Replays a captured Kalanthes MoveTask re-addressed to OUR live entity handle, on the world conversation
		// (WorldFraming + IirCarrier - the same framing every rendered NPC move goes out on).
		private Task SendKalanthesMoveTask(INetworkClient client, byte[] capturedBody)
		{
			var body = (byte[])capturedBody.Clone();
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(8, 4), LiveNpcHandle(client, "Kalanthes", 0x2c1c));
			return new PacketStream()
				.WriteHeader(World.WorldFraming.Sender, World.WorldFraming.Receiver(client), GameServerCarrierOpcodes.IirCarrier)
				.WriteArrayUInt32Length(s => s.WriteArray(body))
				.SendAsync(client);
		}

		// A ScryScriptIIR step on the GameAgent->GameInterface conversation (the framing the verbatim burst uses).
		private static Task SendScry(INetworkClient client, byte[] scry)
		{
			var id = client.Session.Account.ClientInstance;
			return new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameAgentType, 0x08), ConanEndpoints.EndpointBytes(ConanEndpoints.GameInterfaceType, id), GameServerCarrierOpcodes.IirCarrier)
				.WriteArrayUInt32Length(s => s.WriteArray(scry))
				.SendAsync(client);
		}
	}
}
