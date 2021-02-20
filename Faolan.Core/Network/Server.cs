using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Faolan.Core.Network
{
    public abstract class Server<TPacket> : BackgroundService where TPacket : Packet
    {
        private readonly TcpListener _tcpListener;
        protected readonly IConfiguration Configuration;

        protected readonly IDatabaseRepository Database;
        protected readonly ILogger Logger;
        protected readonly SynchronizedCollection<NetworkClient> NetworkClients = new();

        // ReSharper disable once SuggestBaseTypeForParameter
        protected Server(ushort port, ILogger logger, IConfiguration configuration, IDatabaseRepository database)
        {
            Logger = logger;
            Configuration = configuration;
            Database = database;
            _tcpListener = new TcpListener(IPAddress.Any, port);
        }

        public IPEndPoint LocalEndPoint => (IPEndPoint) _tcpListener?.LocalEndpoint;

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            try
            {
                _tcpListener.Start();
                cancellationToken.Register(_tcpListener.Stop);

                Logger.LogInformation($"Started on port: {LocalEndPoint.Port}");
            }
            catch (Exception e)
            {
                Logger.LogError(e, $"Failed to start on port: {LocalEndPoint.Port}");
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
                try
                {
                    var socket = await _tcpListener.AcceptSocketAsync();
                    /*var clientTask = protocol.HandleClient(client, cancellationToken)
                        .ContinueWith(antecedent => client.Dispose())
                        .ContinueWith(antecedent => logger.LogInformation("Client disposed."));*/

                    var networkClient =
                        (NetworkClient) Activator.CreateInstance(typeof(NetworkClient<TPacket>), socket, Logger);
                    if (networkClient == null)
                        throw new Exception("networkClient == null");

                    networkClient.Disconnected = ClientDisconnected;
                    networkClient.ReceivedPacket = (s, e) => ReceivedPacket(s, (TPacket) e);
                    ClientConnected(networkClient);

                    networkClient.Start();
                }
                catch (SocketException) when (cancellationToken.IsCancellationRequested)
                {
                    Logger.LogInformation("Stopped listening because cancellation was requested.");
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error handling client");
                }
        }

        protected void ClientConnected(NetworkClient client)
        {
            NetworkClients.Add(client);
            Logger.LogInformation($"Client with address: {client.IpAddress} connected!");
        }

        protected void ClientDisconnected(NetworkClient client)
        {
            NetworkClients.Remove(client);
            Logger.LogInformation($"Client with address: {client.IpAddress} disconnected!");
        }

        protected abstract Task ReceivedPacket(NetworkClient client, TPacket packet);
    }
}