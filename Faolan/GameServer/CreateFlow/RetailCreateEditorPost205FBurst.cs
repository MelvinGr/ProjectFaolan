using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;

namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		// Verbatim replay of the retail create-editor POST-0x205F burst from
		// Captures/account-stricken998-character-create-no-worldenter.json (rows 100-166, excluding Pong row 146).
		// Retail waits for the first 3x 0x021662C8 after the client's first 0x205F, then answers with this
		// SpawnCheck/0xF98E10B3 burst plus the 0x0009FF92-0x0009FF96 acknowledgement packets.

		private static async Task SendRetailCreateEditorPost205FBurst(INetworkClient client)
		{
			var id = client.Session.Account.ClientInstance;
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceType, id), GameServerRespondOpcodes.SpawnCheck)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x92, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceType, id), GameServerRespondOpcodes.SpawnCheck)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x93, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameInterfaceType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0x00, 0x15, 0xF9, 0x8E, 0x10, 0xB3, 0x00, 0x00, 0xC3, 0x50)
				.WriteUInt32(id)
				.WriteArray(0x01, 0x00, 0x00, 0x00, 0x13, 0x00, 0x00, 0x00, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceType, id), GameServerRespondOpcodes.SpawnCheck)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x94, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameInterfaceType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0x00, 0x15, 0xF9, 0x8E, 0x10, 0xB3, 0x00, 0x00, 0xC3, 0x50)
				.WriteUInt32(id)
				.WriteArray(0x01, 0x00, 0x00, 0x00, 0x13, 0x00, 0x00, 0x00, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceType, id), GameServerRespondOpcodes.SpawnCheck)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x94, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceType, id), GameServerRespondOpcodes.SpawnCheck)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x95, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameInterfaceType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0x00, 0x15, 0xF9, 0x8E, 0x10, 0xB3, 0x00, 0x00, 0xC3, 0x50)
				.WriteUInt32(id)
				.WriteArray(0x01, 0x00, 0x00, 0x00, 0x13, 0x00, 0x00, 0x00, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceType, id), GameServerRespondOpcodes.SpawnCheck)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x96, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameInterfaceType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0x00, 0x15, 0xF9, 0x8E, 0x10, 0xB3, 0x00, 0x00, 0xC3, 0x50)
				.WriteUInt32(id)
				.WriteArray(0x01, 0x00, 0x00, 0x00, 0x13, 0x00, 0x00, 0x00, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceType, id), GameServerRespondOpcodes.SpawnCheck)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x96, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, id), [0x0D, 0xFC, 0x56, 0x97, 0x5B], GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0xC3, 0x50)
				.WriteUInt32(id)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x95)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, id), [0x0D, 0xFC, 0x56, 0x97, 0x5B], GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0xC3, 0x50)
				.WriteUInt32(id)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x96)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, id), [0x0D, 0xFC, 0x56, 0x97, 0x5B], GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0xC3, 0x50)
				.WriteUInt32(id)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x93)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, id), [0x0D, 0xFC, 0x56, 0x97, 0x5B], GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0xC3, 0x50)
				.WriteUInt32(id)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x94)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, id), [0x0D, 0xFC, 0x56, 0x97, 0x5B], GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0xC3, 0x50)
				.WriteUInt32(id)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x92)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, id), [0x0D, 0xFC, 0x56, 0x97, 0x5B], GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0xC3, 0x50)
				.WriteUInt32(id)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x95)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, id), [0x0D, 0xFC, 0x56, 0x97, 0x5B], GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0xC3, 0x50)
				.WriteUInt32(id)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x96)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameInterfaceType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0x00, 0x15, 0xF9, 0x8E, 0x10, 0xB3, 0x00, 0x00, 0xC3, 0x50)
				.WriteUInt32(id)
				.WriteArray(0x01, 0x00, 0x00, 0x00, 0x13, 0x00, 0x00, 0x00, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceType, id), GameServerRespondOpcodes.SpawnCheck)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x94, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameInterfaceType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0x00, 0x15, 0xF9, 0x8E, 0x10, 0xB3, 0x00, 0x00, 0xC3, 0x50)
				.WriteUInt32(id)
				.WriteArray(0x01, 0x00, 0x00, 0x00, 0x13, 0x00, 0x00, 0x00, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceType, id), GameServerRespondOpcodes.SpawnCheck)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x96, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameInterfaceType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0x00, 0x15, 0xF9, 0x8E, 0x10, 0xB3, 0x00, 0x00, 0xC3, 0x50)
				.WriteUInt32(id)
				.WriteArray(0x01, 0x00, 0x00, 0x00, 0x13, 0x00, 0x00, 0x00, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceType, id), GameServerRespondOpcodes.SpawnCheck)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x94, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameInterfaceType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0x00, 0x15, 0xF9, 0x8E, 0x10, 0xB3, 0x00, 0x00, 0xC3, 0x50)
				.WriteUInt32(id)
				.WriteArray(0x01, 0x00, 0x00, 0x00, 0x13, 0x00, 0x00, 0x00, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceType, id), GameServerRespondOpcodes.SpawnCheck)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x96, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameInterfaceType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0x00, 0x15, 0xF9, 0x8E, 0x10, 0xB3, 0x00, 0x00, 0xC3, 0x50)
				.WriteUInt32(id)
				.WriteArray(0x01, 0x00, 0x00, 0x00, 0x13, 0x00, 0x00, 0x00, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceType, id), GameServerRespondOpcodes.SpawnCheck)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x94, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameInterfaceType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0x00, 0x15, 0xF9, 0x8E, 0x10, 0xB3, 0x00, 0x00, 0xC3, 0x50)
				.WriteUInt32(id)
				.WriteArray(0x01, 0x00, 0x00, 0x00, 0x13, 0x00, 0x00, 0x00, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceType, id), GameServerRespondOpcodes.SpawnCheck)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x96, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameInterfaceType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0x00, 0x15, 0xF9, 0x8E, 0x10, 0xB3, 0x00, 0x00, 0xC3, 0x50)
				.WriteUInt32(id)
				.WriteArray(0x01, 0x00, 0x00, 0x00, 0x13, 0x00, 0x00, 0x00, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceType, id), GameServerRespondOpcodes.SpawnCheck)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x94, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameInterfaceType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0x00, 0x15, 0xF9, 0x8E, 0x10, 0xB3, 0x00, 0x00, 0xC3, 0x50)
				.WriteUInt32(id)
				.WriteArray(0x01, 0x00, 0x00, 0x00, 0x13, 0x00, 0x00, 0x00, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceType, id), GameServerRespondOpcodes.SpawnCheck)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x96, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameInterfaceType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0x00, 0x15, 0xF9, 0x8E, 0x10, 0xB3, 0x00, 0x00, 0xC3, 0x50)
				.WriteUInt32(id)
				.WriteArray(0x01, 0x00, 0x00, 0x00, 0x13, 0x00, 0x00, 0x00, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceType, id), GameServerRespondOpcodes.SpawnCheck)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x94, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameInterfaceType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0x00, 0x15, 0xF9, 0x8E, 0x10, 0xB3, 0x00, 0x00, 0xC3, 0x50)
				.WriteUInt32(id)
				.WriteArray(0x01, 0x00, 0x00, 0x00, 0x13, 0x00, 0x00, 0x00, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceType, id), GameServerRespondOpcodes.SpawnCheck)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x96, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameInterfaceType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0x00, 0x15, 0xF9, 0x8E, 0x10, 0xB3, 0x00, 0x00, 0xC3, 0x50)
				.WriteUInt32(id)
				.WriteArray(0x01, 0x00, 0x00, 0x00, 0x13, 0x00, 0x00, 0x00, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceType, id), GameServerRespondOpcodes.SpawnCheck)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x94, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameInterfaceType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0x00, 0x15, 0xF9, 0x8E, 0x10, 0xB3, 0x00, 0x00, 0xC3, 0x50)
				.WriteUInt32(id)
				.WriteArray(0x01, 0x00, 0x00, 0x00, 0x13, 0x00, 0x00, 0x00, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceType, id), GameServerRespondOpcodes.SpawnCheck)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x96, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameInterfaceType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0x00, 0x15, 0xF9, 0x8E, 0x10, 0xB3, 0x00, 0x00, 0xC3, 0x50)
				.WriteUInt32(id)
				.WriteArray(0x01, 0x00, 0x00, 0x00, 0x13, 0x00, 0x00, 0x00, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceType, id), GameServerRespondOpcodes.SpawnCheck)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x94, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameInterfaceType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0x00, 0x15, 0xF9, 0x8E, 0x10, 0xB3, 0x00, 0x00, 0xC3, 0x50)
				.WriteUInt32(id)
				.WriteArray(0x01, 0x00, 0x00, 0x00, 0x13, 0x00, 0x00, 0x00, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceType, id), GameServerRespondOpcodes.SpawnCheck)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x96, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentProjectType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x93, 0x00, 0x00, 0x00, 0x02)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentProjectType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x93, 0x00, 0x00, 0x00, 0x02)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentProjectType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x94, 0x00, 0x00, 0x00, 0x02)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentProjectType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x94, 0x00, 0x00, 0x00, 0x02)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentProjectType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x92, 0x00, 0x00, 0x00, 0x02)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentProjectType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x92, 0x00, 0x00, 0x00, 0x02)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentProjectType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x95, 0x00, 0x00, 0x00, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentProjectType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x95, 0x00, 0x00, 0x00, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentProjectType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x96, 0x00, 0x00, 0x00, 0x02)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentProjectType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x96, 0x00, 0x00, 0x00, 0x02)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentProjectType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x93, 0x00, 0x00, 0x00, 0x02)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentProjectType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x94, 0x00, 0x00, 0x00, 0x02)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentProjectType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x92, 0x00, 0x00, 0x00, 0x02)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentProjectType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x95, 0x00, 0x00, 0x00, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentProjectType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x96, 0x00, 0x00, 0x00, 0x02)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentProjectType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x93, 0x00, 0x00, 0x00, 0x02)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentProjectType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x94, 0x00, 0x00, 0x00, 0x02)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentProjectType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x92, 0x00, 0x00, 0x00, 0x02)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentProjectType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x95, 0x00, 0x00, 0x00, 0x00)
				.SendAsync(client);
			await new PacketStream()
				.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentProjectType, 0x59), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, id), GameServerRespondOpcodes.IirCarrier)
				.WriteArray(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x96, 0x00, 0x00, 0x00, 0x02)
				.SendAsync(client);
		}
	}
}
