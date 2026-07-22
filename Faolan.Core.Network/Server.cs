using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Interfaces;
using Faolan.Core.Network.Logging;
using Faolan.Core.Network.Packets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Faolan.Core.Network
{
	public abstract class Server<TPacket, TPacketFactory, TMessageFramer> : BackgroundService
		where TPacket : Packet where TPacketFactory : IPacketFactory<TPacket>, new() where TMessageFramer : IMessageFramer, new()
	{
		protected readonly IConfiguration Configuration;
		protected readonly IDatabaseRepository Database;
		protected readonly ILogger Logger;

		private readonly ConcurrentDictionary<INetworkClient, byte> _clients = [];
		private readonly IPacketLogger _outgoingPacketLogger;
		private readonly string _channelName;
		private readonly TcpListener _tcpListener;

		public IPEndPoint LocalEndPoint => (IPEndPoint)_tcpListener.LocalEndpoint;
		protected IEnumerable<INetworkClient> NetworkClients => _clients.Keys;

		protected Server(ushort port, ILogger logger, IConfiguration configuration, IDatabaseRepository database, IPacketLogger? outgoingPacketLogger = null)
		{
			Logger = logger;
			Configuration = configuration;
			Database = database;
			_outgoingPacketLogger = outgoingPacketLogger ?? NullPacketLogger.Instance;
			// Reuse the outbound logger's channel name for the inbound (UL) lines, so both directions read "[Channel …]".
			_channelName = (outgoingPacketLogger as OutgoingPacketLogger)?.ChannelName ?? "";
			_tcpListener = new TcpListener(IPAddress.Any, port);
		}

		protected override async Task ExecuteAsync(CancellationToken cancellationToken)
		{
			try
			{
				_tcpListener.Start();
				cancellationToken.Register(_tcpListener.Stop);

				Logger.LogInformation("Listening on port {Port}.", LocalEndPoint.Port);
			}
			catch (Exception e)
			{
				Logger.LogError(e, "Failed to start listener on port {Port}.", LocalEndPoint.Port);
				return;
			}

			while (!cancellationToken.IsCancellationRequested)
			{
				try
				{
					var socket = await _tcpListener.AcceptSocketAsync(cancellationToken);

					// Disable Nagle. The client frames the uncompressed world-entry burst per TCP segment (no
					// reassembly), so it needs one-packet-per-segment. With Nagle on, rapid small burst packets get
					// coalesced into segments by ACK/CPU timing, which varies run-to-run - intermittently misframing
					// the burst and hanging the client on the loading screen. NoDelay flushes each send as its own
					// segment, making the segmentation deterministic.
					socket.NoDelay = true;

					var networkClient = new NetworkClient<TPacket, TMessageFramer, TPacketFactory>(socket, Logger, _outgoingPacketLogger)
					{
						Disconnected = async client =>
						{
							try
							{
								_clients.TryRemove(client, out _);
								await ClientDisconnected(client);
							}
							catch (Exception ex)
							{
								Logger.LogError(ex, "Exception in Disconnected");
							}
						},
						ReceivedPacket = async (client, packet) =>
						{
							try
							{
								await ReceivedPacket(client, (TPacket)packet);
							}
							catch (Exception ex)
							{
								Logger.LogError(ex, "Exception in ReceivedPacket");
								client.Stop();
							}
						},
						ReceivedBatchComplete = async client =>
						{
							try
							{
								await ReceivedBatchComplete(client);
							}
							catch (Exception ex)
							{
								Logger.LogError(ex, "Exception in ReceivedBatchComplete");
								client.Stop();
							}
						}
					};

					_clients.TryAdd(networkClient, 0);
					await ClientConnected(networkClient);

					networkClient.Start();
				}
				catch (SocketException) when (cancellationToken.IsCancellationRequested)
				{
					Logger.LogInformation("Listener stopped because cancellation was requested.");
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					Logger.LogInformation("Listener stopped because cancellation was requested.");
				}
				catch (Exception ex)
				{
					Logger.LogError(ex, "Error handling client");
				}
			}
		}

		protected virtual Task ClientConnected(INetworkClient client)
		{
			Logger.LogInformation("Client connected: {IpAddress}", client.IpAddress);
			return Task.CompletedTask;
		}

		protected virtual Task ClientDisconnected(INetworkClient client)
		{
			Logger.LogInformation("Client disconnected: {IpAddress}", client.IpAddress);
			return Task.CompletedTask;
		}

		// Called once after each recv batch's packets are all dispatched (see NetworkClient.ReceivedBatchComplete).
		// Default no-op; the GameServer overrides it to fire the create-editor post-morph burst deterministically
		// (#197) instead of the debounce timer. Other listeners are unaffected.
		protected virtual Task ReceivedBatchComplete(INetworkClient client)
		{
			return Task.CompletedTask;
		}

		protected INetworkClient? ClientForAccount(uint accountId)
		{
			return NetworkClients.FirstOrDefault(nc => nc.Session.Account?.Id == accountId);
		}

		protected INetworkClient? ClientForCharacter(uint characterId)
		{
			return NetworkClients.FirstOrDefault(nc => nc.Session.Character?.Id == characterId);
		}

		// Extra, opcode-specific body dump some inbound handlers emit on their own line. Gated on the global
		// LogPacketBytes switch so disabling packet bytes silences these too.
		protected void LogPacketBodyDebug(string label, byte[] bytes)
		{
			if (bytes?.Length > 0 && PacketLogOptions.ShouldLogBytes(Logger))
				Logger.LogDebug("{Label} ({Length} bytes): {Bytes}", label, bytes.Length, bytes.ToHex());
		}

		protected void LogPacketReceived(Packet packet, Enum opcode)
		{
			Logger.LogPacket(PacketLogFormatter.Uplink, _channelName, PacketLogFormatter.OpcodeName(opcode), packet.Body?.Length ?? 0, packet.Bytes?.Length ?? 0,
				("body", packet.Body?.ToArray()));
		}

		protected void LogUnknownPacket(Packet packet, Enum opcode)
		{
			var opcodeName = Enum.IsDefined(opcode.GetType(), opcode)
				? opcode.ToString()
				: "Unknown";

			Logger.LogWarning("Unhandled packet [{OpcodeName} ({OpcodeHex})], bodyLength={BodyLength}, packetLength={PacketLength}{Bytes}.", opcodeName, opcode.ToHex(), packet.Body?.Length ?? 0,
				packet.Bytes?.Length ?? 0, PacketLogOptions.BytesSuffix(Logger, ("body", packet.Body?.ToArray())));
		}

		protected void LogUnknownPacket(Packet packet, ushort opcode, string opcodeName = "Unknown")
		{
			Logger.LogWarning("Unhandled packet [{OpcodeName} ({OpcodeHex})], bodyLength={BodyLength}, packetLength={PacketLength}{Bytes}.", opcodeName, opcode.ToHex(), packet.Body?.Length ?? 0,
				packet.Bytes?.Length ?? 0, PacketLogOptions.BytesSuffix(Logger, ("body", packet.Body?.ToArray())));
		}

		protected abstract Task ReceivedPacket(INetworkClient client, TPacket packet);
	}
}
