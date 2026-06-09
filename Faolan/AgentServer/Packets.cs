using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Data;
using Faolan.Core.Network;
using Faolan.Core.Network.Packets.Opcodes;
using Microsoft.Extensions.Logging;

namespace Faolan.AgentServer
{
	partial class AgentServerListener
	{
		public async Task CharacterSay(uint characterId, string message)
		{
			// should only send to nearby players, but this will do for now.
			foreach (var client in NetworkClients)
			{
				await new FaolanStream().WriteUInt16(AgentServerRespondsOpcodes.CharacterChat) // opcode
					.WriteArrayUInt16Length(s => s.WriteUInt32(characterId).WriteString(message).WriteUInt16(0x0001) // type?
						.WriteByte(0)).SendAsync(client);
			}
		}

		public async Task SendSystemMessage(uint characterId, string message)
		{
			var client = ClientForCharacter(characterId);
			if (client == null)
			{
				Logger.LogInformation("Skipping AgentServer system message for character {CharacterId:X8} because no AgentServer client is connected yet. Message: {Message}", characterId, message);
				return;
			}

			await SendSystemMessage(client, message);
		}

		public Task SystemBroadcast(string message)
		{
			return Task.WhenAll(NetworkClients.Select(c => SendSystemMessage(c, message)));
		}

		private Task SendSystemMessage(INetworkClient client, string message)
		{
			return new FaolanStream().WriteUInt16(AgentServerRespondsOpcodes.SystemMessage).WriteArrayUInt16Length(s => s.WriteUInt16(0x0000).WriteString(message).WriteUInt16(0x0000)).SendAsync(client);
		}

		// A chat-channel subscription (0x003C): [type][id][name][flag][0x0000]. Sent on AgentServer connect for each
		// channel the character joins (~Trial/~Global/~LFG/~GlobalEnglish/~Playfield/~NewbieHelp). Byte layout
		// verified from the raw login pcap.
		private Task SendChannelSubscription(INetworkClient client, byte type, uint id, string name, uint flag)
		{
			return new FaolanStream().WriteUInt16(AgentServerRespondsOpcodes.Ox003C)
				.WriteArrayUInt16Length(s => s.WriteByte(type).WriteUInt32(id).WriteString(name).WriteUInt32(flag).WriteUInt16(0x0000)).SendAsync(client);
		}
	}
}
