using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Faolan.Core.Database;

namespace Faolan.Core.Network
{
    public abstract class Server<TPacketType>
        where TPacketType : Packet
    {
        private readonly Task _listenThread;
        private readonly TcpListener _tcpListener;
        public readonly IDatabase Database;
        protected readonly Logger Logger;
        public readonly ushort Port;

        protected Server(ushort port, Logger logger, IDatabase database)
        {
            if (logger == null || database == null)
                throw new Exception("logger == null || database == null");

            Port = port;
            Logger = logger;
            Database = database;
            _tcpListener = new TcpListener(IPAddress.Any, Port);

            _listenThread = new Task(async () =>
            {
                try
                {
                    _tcpListener.Start(100);
                    Logger.Info("Started on port: {0}", Port);
                }
                catch (Exception e)
                {
                    Logger.Error("Failed to start on port: {0}\r\n{1}", Port, e.Message);
                    return;
                }

                Logger.Info("Waiting for a connection...");
                while (KeepRunning)
                    try
                    {
                        var soc = await _tcpListener.AcceptSocketAsync();
                        var state =
                            (INetworkClient) Activator.CreateInstance(typeof(NetworkClient<TPacketType>), soc, this);
                        NetworkClients.Add(state);
                        ClientConnected(state);
                    }
                    catch (Exception e)
                    {
                        //
                    }
            });
        }

        public bool KeepRunning { get; private set; }

        public static List<INetworkClient> NetworkClients { get; } = new List<INetworkClient>();

        public void Start()
        {
            _listenThread.Start();
        }

        public void Stop()
        {
            KeepRunning = false;
            _tcpListener.Stop();
            //_listenThread.Abort();
        }

        public virtual void ClientConnected(INetworkClient client)
        {
            Logger.Info($"Client with address: {client.IpAddress} connected!");
        }

        public virtual void ClientDisconnected(INetworkClient client)
        {
            Logger.Info($"Client with address: {client.IpAddress} disconnected!");
        }

        public abstract void ReceivedPacket(INetworkClient client, TPacketType packet);
    }
}