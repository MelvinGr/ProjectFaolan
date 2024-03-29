using System.Linq;
using Faolan.Core.Data;
using Faolan.Core.Network;
using Faolan.Core.Network.Opcodes;

namespace Faolan.AgentServer
{
	partial class AgentServerListener
	{
		public void SendSystemMessage(Account account, string message)
		{
			var client = ClientForAccount(account);
			if (client != null)
				SendSystemMessage(client, message);
		}

		public void SystemBroadcast(string message)
		{
			foreach (var client in NetworkClients)
				SendSystemMessage(client, message);
		}

		public void CharacterSay(Account account, string message)
		{
			// should only send to nearby players, but this will do for now.
			foreach (var client in NetworkClients)
			{
				new ConanStream()
					.WriteUInt16(AgentServerRespondsOpcodes.CharacterChat) // opcode
					.WriteArrayUInt16Length(new ConanStream()
						.WriteUInt32(client.Account.ClientInstance)
						.WriteString(message)
						.WriteUInt16(0x0001) // type?
						.WriteByte(0))
					.Send(client);
			}
		}

		private INetworkClient ClientForAccount(Account account)
		{
			return NetworkClients.FirstOrDefault(nc => nc.Account.ClientInstance == account.ClientInstance);
		}

		private void SendSystemMessage(INetworkClient client, string message)
		{
			new ConanStream()
				.WriteUInt16(AgentServerRespondsOpcodes.SystemMessage)
				.WriteArrayUInt16Length(new ConanStream()
					.WriteUInt16(0x0000)
					.WriteString(message)
					.WriteUInt16(0x0000))
				.Send(client);
		}
	}
}
