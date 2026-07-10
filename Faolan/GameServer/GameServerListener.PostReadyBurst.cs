using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Packets;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		// The post-0x2024 login record stream (world state / quest / UI) retail sends right after ReadyForPlayScreen
		// + abilities - the records that let the client leave the loading screen. Rebuilt from the Strie 4010 login
		// capture as explicit PacketStream sends: the live player instance is written inline (the receiver is built via
		// Endpoints.EndpointBytes(type, instance), WriteUInt32(instance) where the body carries it), so no ids are
		// hard-coded. The remaining body
		// bytes are opaque captured payloads (quest/UI blobs). TODO: per-map/DB-driven construction, like the world content.
		// #304: the legacy verbatim beach NPC records (spawns/MoveTasks/despawns/BehaviorCC on captured handles) were
		// deleted - WorldBroadcaster interest streaming owns the beach population, confirmed in-client.
		private async Task SendPostReadyBurst(INetworkClient client)
		{
			var instance = client.Session.Account.ClientInstance;

			await new PacketStream()
				.WriteHeader(WorldFraming.Sender, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(CoreClaimCodec.Build(client.Session.Account.ClientInstance))
				.SendAsync(client);

			// record 0: 0x00002000, 17-byte body
			await new PacketStream()
				.WriteHeader(WorldFraming.Sender, Endpoints.EndpointBytes(Endpoints.GameInterfaceType, instance), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArray(0x00, 0x00, 0x00, 0x0D, 0x86, 0x4C, 0xFE, 0xF8, 0x00, 0x00, 0xC3, 0x50)
				.WriteUInt32(instance)
				.WriteArray(0x00)
				.SendAsync(client);

			// record 1: 0x0000200C, 1-byte body
			await new PacketStream()
				.WriteHeader(ExistingCharacterPostReadyReportServerIdSender, Endpoints.EndpointBytes(Endpoints.GameCharInterfaceProjectType, instance),
					GameInterfaceDispatchOpcodes.DispatchReportServerID)
				.WriteArray(0x01)
				.SendAsync(client);

			// record 2: 0x00002000 - the StartFixed3d destiny step (ScryScriptIIR 0xA36D3B74), constructed (was verbatim;
			// byte-identical, proven by QuestTests.BuildScryStep_MatchesCapture). The body is [u32 len][step bytes].
			// #234: SKIP on the create-world connection - the create destiny cutscene overlay (SendCreateCutsceneStep)
			// already sends StartFixed3d at IntroDone (matching retail, which sends it ONCE there). Sending it here too
			// (before the cutscene) is a DUPLICATE retail doesn't have (loopback f649) and re-engages the cinematic.
			if (!client.Session.IsCreateWorldSession)
			{
				var startFixed3d = ScryScript.BuildStep(instance, "StartFixed3d");
				await new PacketStream()
					.WriteHeader(WorldFraming.Sender, Endpoints.EndpointBytes(Endpoints.GameInterfaceType, instance), GameInterfaceDispatchOpcodes.DispatchPassBlob)
					.WriteUInt32(startFixed3d.Length)
					.WriteArray(startFixed3d)
					.SendAsync(client);
			}

			// record 2b (BEACH ONLY, #267): the destiny SCRIPT GROUP that builds the chained-Casilda tableau - it
			// renders the `hangingwoman` chains and sets up the intro confinement ("invisible wall"). Retail sends
			// StartScriptGroupOnClient(385910) on EVERY Tortage-beach LOGIN, between StartFixed3d and WelcomeTutorial
			// (decode_scryscript.py on Ranochar/Stihetfv beach logins). We omitted it, which is why our beach chains
			// don't instantiate (the dynel is byte-exact + its mesh 347243 is in the RDB, but nothing runs the scene
			// script) and why the gate never resolves. Gated to the beach so a non-Tortage login isn't steered into
			// the intro destiny flow.
			var onBeach = client.Session.Character?.MapId == Map.TortageBeachId;

			// record 3: 0x00002000, 35-byte body - the GameCode script-message (script id 0x5F376, no name string, 			// unlike record 2 StartFixed3d / record 4 WelcomeTutorial) that triggers the on-screen TUTORIAL overlay.
			// DISABLED (if(false)) so the tutorial no longer pops on world entry; re-enable to restore it. (Note: this
			// is NOT the item-shop window - that one is opened client-side and persists even with the whole post-ready
			// burst removed, so it can't be suppressed from here.)
			if (false)
			{
				await new PacketStream()
					.WriteHeader(WorldFraming.Sender, Endpoints.EndpointBytes(Endpoints.GameInterfaceType, instance), GameInterfaceDispatchOpcodes.DispatchPassBlob)
					.WriteArray(0x00, 0x00, 0x00, 0x1F, 0xA3, 0x6D, 0x3B, 0x74, 0x00, 0x00, 0xC3, 0x50)
					.WriteUInt32(instance)
					.WriteArray(0x00, 0x00, 0x00, 0x00, 0x0E, 0x08, 0x05, 0x10, 0x00, 0x18, 0xF6, 0xC6, 0x17, 0x32, 0x04, 0x08, 0x00, 0x10, 0x00)
					.SendAsync(client);

				// record 4: 0x00002000 - the WelcomeTutorial destiny step (ScryScriptIIR 0xA36D3B74), constructed (was
				// verbatim; byte-identical, proven by QuestTests.BuildScryStep_MatchesCapture).
				var welcomeTutorial = ScryScript.BuildStep(instance, "WelcomeTutorial");
				await new PacketStream()
					.WriteHeader(WorldFraming.Sender, Endpoints.EndpointBytes(Endpoints.GameInterfaceType, instance), GameInterfaceDispatchOpcodes.DispatchPassBlob)
					.WriteUInt32(welcomeTutorial.Length)
					.WriteArray(welcomeTutorial)
					.SendAsync(client);
			}

			// #267: if this character has ALREADY freed Casilda (relog after the beach talk stage), drop the chain-wall
			// collision up-front so they aren't re-blocked by the world-entry burst's chain dynel.
			if (onBeach)
				await _broadcaster.SendBeachGateOnEntry(client);

			Logger.LogInformation("Sent the post-0x2024 login records.");
		}
	}
}
