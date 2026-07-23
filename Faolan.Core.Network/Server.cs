using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Network.Interfaces;
using Faolan.Core.Network.Logging;
using Faolan.Core.Network.Packets.ChatServer;
using Faolan.Core.Network.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Faolan.Core.Network
{
	// The shared listener machinery - TCP accept loop, client registry, connect/disconnect lifecycle - independent of
	// how a frame is parsed. ChatServerBase (the chat protocol, a typed Packet) and RpcServer<TFramer> (every Conan
	// protocol listener, the structured Rpc) supply CreateClient + their ReceivedPacket, and each holds the concrete
	// tracer its protocol traces BOTH directions through (the RX half is protocol-typed, so it lives there, not here).
	public abstract class ServerBase : BackgroundService
	{
		protected readonly IConfiguration Configuration;
		protected readonly IDatabaseRepository Database;
		protected readonly ILogger Logger;

		private readonly ConcurrentDictionary<INetworkClient, byte> _clients = [];
		private readonly TcpListener _tcpListener;

		public IPEndPoint LocalEndPoint => (IPEndPoint)_tcpListener.LocalEndpoint;
		protected IEnumerable<INetworkClient> NetworkClients => _clients.Keys;

		protected ServerBase(ushort port, ILogger logger, IConfiguration configuration, IDatabaseRepository database)
		{
			Logger = logger;
			Configuration = configuration;
			Database = database;
			_tcpListener = new TcpListener(IPAddress.Any, port);
		}

		// Build the protocol-specific client for the accepted socket, wired with the shared lifecycle callbacks
		// (OnClientDisconnected / OnBatchComplete) plus its own receive dispatch.
		protected abstract NetworkClient CreateClient(Socket socket);

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

					var networkClient = CreateClient(socket);

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

		// The shared disconnect/batch-drained callbacks a CreateClient wires onto its NetworkClient.
		protected async Task OnClientDisconnected(INetworkClient client)
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
		}

		protected async Task OnBatchComplete(INetworkClient client)
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

		// An extra opcode-specific body dump a handler emits on its own line, on the same byte-dump gate as the packet
		// lines - all line shaping lives in the logging layer.
		protected void LogPacketBodyDebug(string label, ReadOnlyMemory<byte> bytes)
		{
			Logger.LogBodyDump(label, bytes);
		}
	}

	// The chat-protocol listener (ChatServer): each frame is parsed to a ChatServerMessage and dispatched. The chat wire
	// frame ([u16 opcode][u16 bodyLen][body]) has no Rpc sender/receiver/CRC model, so it keeps its own typed Packet +
	// framer rather than the structured Rpc path. This is the sole typed-Packet tier, so it is a plain concrete base (no
	// generic TPacket/TFactory) - the packet is constructed directly by ChatNetworkClient, so there is no IPacketFactory.
	public abstract class ChatServerBase : ServerBase
	{
		// This channel's packet trace. The listener traces its RECEIVES through it and hands it to every client so the
		// SENDS are traced through the same object, with the same channel label (Gitea #406 phase 6).
		private readonly ChatPacketTracer _tracer;

		protected ChatServerBase(ushort port, ILogger logger, IConfiguration configuration, IDatabaseRepository database, ChatPacketTracer tracer)
			: base(port, logger, configuration, database)
		{
			_tracer = tracer;
		}

		protected override NetworkClient CreateClient(Socket socket)
		{
			return new ChatNetworkClient(socket, Logger, _tracer)
			{
				Disconnected = OnClientDisconnected,
				ReceivedBatchComplete = OnBatchComplete,
				ReceivedPacket = async (client, packet) =>
				{
					try
					{
						await ReceivedPacket(client, packet);
					}
					catch (Exception ex)
					{
						Logger.LogError(ex, "Exception in ReceivedPacket");
						client.Stop();
					}
				}
			};
		}

		// Inbound (RX) trace, through the SAME tracer the transport traces this channel's sends with: the method name and
		// the frame length both come off the parsed message, so the listener passes nothing but the packet.
		protected void LogPacketReceived(in ChatServerMessage packet)
		{
			_tracer.Received(in packet);
		}

		protected void LogUnknownPacket(in ChatServerMessage packet)
		{
			_tracer.Unhandled(in packet);
		}

		protected abstract Task ReceivedPacket(INetworkClient client, ChatServerMessage packet);
	}

	// The structured-Rpc listener (every Conan protocol tier): each frame is parsed straight to an Rpc (Rpc.Parse)
	// and dispatched - no per-frame Packet object, no ConanPacket (Gitea #406/#407).
	public abstract class RpcServer<TMessageFramer> : ServerBase
		where TMessageFramer : IMessageFramer, new()
	{
		// This channel's packet trace - both directions through one object (see ChatServerBase._tracer).
		private readonly ConanPacketTracer _tracer;

		protected RpcServer(ushort port, ILogger logger, IConfiguration configuration, IDatabaseRepository database, ConanPacketTracer tracer)
			: base(port, logger, configuration, database)
		{
			_tracer = tracer;
		}

		protected override NetworkClient CreateClient(Socket socket)
		{
			return new RpcNetworkClient<TMessageFramer>(socket, Logger, _tracer)
			{
				Disconnected = OnClientDisconnected,
				ReceivedBatchComplete = OnBatchComplete,
				ReceivedRpc = async (client, packet) =>
				{
					try
					{
						await ReceivedPacket(client, packet);
					}
					catch (Exception ex)
					{
						Logger.LogError(ex, "Exception in ReceivedPacket");
						client.Stop();
					}
				}
			};
		}

		// Inbound (RX) trace, through the SAME tracer the transport traces this channel's sends with (Gitea #406/#407):
		// both the method NAME (from the Receiver.Type + Opcode pair, via ProtocolCatalog) and the FrameLength (total
		// wire size, reported as packetLength) come straight off the parsed Rpc - the listener passes only the packet.
		protected void LogPacketReceived(in Rpc packet)
		{
			_tracer.Received(in packet);
		}

		protected void LogUnknownPacket(in Rpc packet)
		{
			_tracer.Unhandled(in packet);
		}

		protected abstract Task ReceivedPacket(INetworkClient client, Rpc packet);
	}
}
