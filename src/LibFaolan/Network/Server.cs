using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Hik.Communication.Scs.Communication.EndPoints.Tcp;
using Hik.Communication.Scs.Communication.Messages;
using Hik.Communication.Scs.Communication.Protocols;
using Hik.Communication.Scs.Server;
using LibFaolan.Data;
using LibFaolan.Database;
using LibFaolan.Other;

namespace LibFaolan.Network
{
    public abstract class Server<TPacketType, TWireProtocolFactory>
        where TPacketType : IScsMessage
        where TWireProtocolFactory : IScsWireProtocolFactory, new()
    {
        private readonly IScsServer _scsServer;
        public readonly IDatabase Database;
        public readonly Logger Logger;
        public readonly ushort Port;

        protected Server(ushort port, Logger logger, IDatabase database)
        {
            if (logger == null || database == null)
                throw new Exception("logger == null || database == null");

            Port = port;
            Logger = logger;
            Database = database;

            _scsServer = ScsServerFactory.CreateServer(new ScsTcpEndPoint(port));
            _scsServer.WireProtocolFactory = new TWireProtocolFactory();
            _scsServer.ClientConnected += Internal_ClientConnected;
            _scsServer.ClientDisconnected += (s, e) => ClientDisconnected((NetworkClient) e.Client.Tag);
        }

        protected ConcurrentDictionary<long, IScsServerClient> Clients => _scsServer.Clients;
        protected IEnumerable<NetworkClient> NetworkClients => Clients.Select(c => (NetworkClient)c.Value.Tag);
        protected IEnumerable<Account> Accounts => NetworkClients.Select(nc => (Account)nc.Tag);

        public bool Start()
        {
            try
            {
                _scsServer.Start();
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
                _scsServer.Stop();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void Internal_ClientConnected(object sender, ServerClientEventArgs e)
        {
            e.Client.Tag = new NetworkClient(e.Client);
            e.Client.MessageReceived += (s, ee) =>
                ReceivedPacket((NetworkClient) ((IScsServerClient) s).Tag, (TPacketType) ee.Message);

            ClientConnected((NetworkClient) e.Client.Tag);
        }

        public virtual void ClientConnected(NetworkClient client)
        {
            Logger.WriteLine("Client with address: " + client.IpAddress + " connected!");
        }

        public virtual void ClientDisconnected(NetworkClient client)
        {
            Logger.WriteLine("Client with address: " + client.IpAddress + " disconnected!");
        }

        public abstract void ReceivedPacket(NetworkClient client, TPacketType packet);
    }
}