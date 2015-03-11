using System.Linq;
using LibFaolan.Data;
using LibFaolan.Network;

namespace AgentServer
{
    partial class AgentServerListener
    {
        /*var scsclient = Clients.FirstOrDefault(c =>
        {
            var cc = (NetworkClient)c.Tag;
            var a = (Account)cc.Tag;
            return a.ClientInstance == account.ClientInstance;
        });*/

        public void SendSystemMessage(Account account, string message)
        {
            var client = NetworkClients.FirstOrDefault(nc => ((Account) nc.Tag).ClientInstance == account.ClientInstance);
            if (client != null)
                SendSystemMessage(client, message);
        }

        private void SendSystemMessage(NetworkClient client, string message)
        {
            new ConanStream()
                .WriteUInt16(0x0023)
                .WriteArrayPrependLengthUInt16(new ConanStream()
                    .WriteUInt16(0x0000)
                    .WriteString(message)
                    .WriteUInt16(0x0000))
                .Send(client);
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
                    .WriteUInt16(0x0022) // opcode
                    .WriteArrayPrependLengthUInt16(new ConanStream()
                        .WriteUInt32(account.ClientInstance)
                        .WriteString(message)
                        .WriteUInt16(0x0001) // type?
                        .WriteByte(0))
                    .Send(client);
            }
        }
    }
}