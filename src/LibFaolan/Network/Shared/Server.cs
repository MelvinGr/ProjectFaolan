using System.Collections.Generic;
using System.Linq;
using Hik.Communication.Scs.Communication.EndPoints.Tcp;
using Hik.Communication.Scs.Server;
using LibFaolan.Database;

namespace LibFaolan.Network.Shared
{
    public abstract class Server<TPacketType>
        where TPacketType : Packet
    {
        protected Server(ushort port, Logger logger, IDatabase database)
        {
            Port = port;
            Logger = logger;
            Database = database;
            Clients = new SynchronizedCollection<NetworkClient>();

            ScsServer = ScsServerFactory.CreateServer(new ScsTcpEndPoint(port));
            ScsServer.WireProtocolFactory = new ProtocolFactory<TPacketType>();
            ScsServer.ClientConnected += Internal_ClientConnected;
            ScsServer.ClientDisconnected += Internal_ClientDisconnected;
        }

        public IScsServer ScsServer { get; private set; }
        public ushort Port { get; private set; }
        public SynchronizedCollection<NetworkClient> Clients { get; private set; }
        public Logger Logger { get; set; }
        public IDatabase Database { get; set; }

        public bool Start()
        {
            try
            {
                ScsServer.Start();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool Stop()
        {
            try
            {
                ScsServer.Stop();
                Clients = null;
                ScsServer = null;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void Internal_ClientDisconnected(object sender, ServerClientEventArgs e)
        {
            var client = Clients.FirstOrDefault(c => c.InternalClient.ClientId == e.Client.ClientId);
            ClientDisconnected(client);
            Clients.Remove(client);
        }

        private void Internal_ClientConnected(object sender, ServerClientEventArgs e)
        {
            var client = new NetworkClient(e.Client);
            Clients.Add(client);
            ClientConnected(client);

            e.Client.MessageReceived += (s, ee) => ReceivedPacket(client, (TPacketType) ee.Message);
        }

        public virtual void ClientConnected(NetworkClient client)
        {
        }

        public virtual void ClientDisconnected(NetworkClient client)
        {
        }

        public abstract void ReceivedPacket(NetworkClient client, TPacketType packet);
    }
}