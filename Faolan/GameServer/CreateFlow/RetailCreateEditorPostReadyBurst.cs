using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Opcodes;
using Faolan.Core.Network.Packets;

namespace Faolan.GameServer.CreateFlow
{
	public partial class CreateFlowService
	{
		private static readonly byte[] RetailCreateEditorPostReadyAckSender = Endpoints.EndpointBytes(Endpoints.GameCharAgentProjectType, 0x59);

		private static uint GetRetailCreateEditorPostReadyAckStatus(uint requestId)
		{
			return requestId == 0x0009FF95
				? 0x00000000u
				: 0x00000002u;
		}

		internal static Task SendRetailCreateEditorPostReadyAck(INetworkClient client, uint requestId)
		{
			return new PacketStream()
				.WriteHeader(RetailCreateEditorPostReadyAckSender, Endpoints.EndpointBytes(Endpoints.GameCharInterfaceProjectType, client.Session.Account.ClientInstance), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteUInt32(0x0000C350)
				.WriteUInt32(requestId)
				.WriteUInt32(GetRetailCreateEditorPostReadyAckStatus(requestId))
				.SendAsync(client);
		}

		// The racial CHARACTER-TEMPLATE choice list the creation editor reads (Male/Female/StygianSlaver ->
		// template ids 50000:65525x), delivered on two GameActionListIIR (0xF508F4C1) records for dynel 0xD00C.
		// The two captured variants differ ONLY in the leading action-id word; the property-map action-tail
		// grammar is still opaque (#190) so it rides verbatim, while the record HEADER is constructed via
		// GameActionListCodec.BuildBody (construct-don't-replay - see SendGameActionListRecord).
		private static readonly byte[] CreateEditorTemplateChoiceActionTailA =
		[
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x09, 0x00, 0x39, 0xBF, 0x93, 0x00, 0x50, 			0x4D, 0x61, 0x6C, 0x65, 0x54, 0x65, 0x6D, 0x70, 0x6C, 0x61, 0x74, 0x65, 0x3D, 0x35, 0x30, 0x30, 0x30, 0x30, 			0x3A, 0x36, 0x35, 0x35, 0x32, 0x35, 0x32, 0x3B, 0x46, 0x65, 0x6D, 0x61, 0x6C, 0x65, 0x54, 0x65, 0x6D, 0x70, 			0x6C, 0x61, 0x74, 0x65, 0x3D, 0x35, 0x30, 0x30, 0x30, 0x30, 0x3A, 0x36, 0x35, 0x35, 0x32, 0x35, 0x33, 0x3B, 			0x53, 0x74, 0x79, 0x67, 0x69, 0x61, 0x6E, 0x53, 0x6C, 0x61, 0x76, 0x65, 0x72, 0x3D, 0x35, 0x30, 0x30, 0x30, 			0x30, 0x3A, 0x36, 0x35, 0x35, 0x32, 0x35, 0x34, 0x00, 0x00, 0x00, 0x00, 0x00, 		];

		private static readonly byte[] CreateEditorTemplateChoiceActionTailB =
		[
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x09, 0x00, 0x01, 0x9D, 0xD1, 0x00, 0x50, 			0x4D, 0x61, 0x6C, 0x65, 0x54, 0x65, 0x6D, 0x70, 0x6C, 0x61, 0x74, 0x65, 0x3D, 0x35, 0x30, 0x30, 0x30, 0x30, 			0x3A, 0x36, 0x35, 0x35, 0x32, 0x35, 0x32, 0x3B, 0x46, 0x65, 0x6D, 0x61, 0x6C, 0x65, 0x54, 0x65, 0x6D, 0x70, 			0x6C, 0x61, 0x74, 0x65, 0x3D, 0x35, 0x30, 0x30, 0x30, 0x30, 0x3A, 0x36, 0x35, 0x35, 0x32, 0x35, 0x33, 0x3B, 			0x53, 0x74, 0x79, 0x67, 0x69, 0x61, 0x6E, 0x53, 0x6C, 0x61, 0x76, 0x65, 0x72, 0x3D, 0x35, 0x30, 0x30, 0x30, 			0x30, 0x3A, 0x36, 0x35, 0x35, 0x32, 0x35, 0x34, 0x00, 0x00, 0x00, 0x00, 0x00, 		];

		// Emits a GameActionListIIR (0xF508F4C1) carrier for `dynel`, delivered to frame `id`. The fixed record
		// header (subop/type/Head/four/one, decoded 570/570) is CONSTRUCTED via GameActionListCodec.BuildBody and
		// the still-opaque property-map action tail rides verbatim; the carrier length is computed from the body.
		private static Task SendGameActionListRecord(INetworkClient client, byte agentSub, uint id, uint dynel, byte[] actionTail)
		{
			var body = GameActionListCodec.BuildBody(id, dynel, 0u, actionTail);
			return new PacketStream()
				.WriteHeader(Endpoints.EndpointBytes(Endpoints.GameAgentType, agentSub), Endpoints.EndpointBytes(Endpoints.GameInterfaceType, id), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(body)
				.SendAsync(client);
		}

		// Verbatim replay of the fixed tail of the retail create-editor post-ready follow-up from
		// Captures/account-stricken998-character-create-no-worldenter.json (rows 198-199, 203-204).
		// The leading 0x0009FF92-0x0009FF96 ack packets are generated dynamically from the request ids the
		// client actually sends so the live flow can advance even when it stalls before all 5 retail requests.

		internal static async Task SendRetailCreateEditorPostReadyBurst(INetworkClient client, IReadOnlyList<uint> requestIds)
		{
			foreach (var requestId in requestIds)
				await SendRetailCreateEditorPostReadyAck(client, requestId);

			var id = client.Session.Account.ClientInstance;
			await new PacketStream()
				.WriteHeader(Endpoints.EndpointBytes(Endpoints.GameCharAgentType, 0x59), Endpoints.EndpointBytes(Endpoints.GameCharInterfaceType, id), GameCharInterfaceDispatchOpcodes.DispatchValidResurrectionSpots)
				.WriteArray(0x43, 0xA7, 0x40, 0x00, 0x43, 0x14, 0x66, 0x66, 0x44, 0x25, 0x4C, 0xCD, 0x00, 0x00, 0x07, 0xE2, 0x43, 0xA7, 0x40, 0x00, 0x43, 0x14, 0x66, 0x66, 0x44, 0x25, 0x4C, 0xCD, 0x00, 0x00, 0xC7, 0x9E, 0x00, 0x00, 0x0F, 0xAA, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x64, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(Endpoints.EndpointBytes(Endpoints.GameAgentType, 0x59), Endpoints.EndpointBytes(Endpoints.GameInterfaceType, id), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s.WriteUInt32(GameServer0X2000Opcodes.CharInPlayIIR).WriteArray(CreateEditorRecordBuilder.BuildCharInPlayIir(id)))
				.SendAsync(client);
			await SendGameActionListRecord(client, 0x59, id, 0xD00Cu, CreateEditorTemplateChoiceActionTailA);
			await SendGameActionListRecord(client, 0x59, id, 0xD00Cu, CreateEditorTemplateChoiceActionTailB);

			// After the post-ready editor handoff, retail streams the character-creation script burst
			// (rows 274-331) that actually launches the Slave Galley creation scene via the
			// 0xA36D3B74 "NEW_character_creation" remoteformat. Without it the playfield loads (audio)
			// but the editor never appears (black screen).
			await SendRetailCreateEditorCreationScriptBurst(client);
		}
	}
}
