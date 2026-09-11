using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Cli;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Network.Logging;
using Faolan.Core.Network.Packets.ChatServer;
using Faolan.Core.Network.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Faolan.Core.Network
{
	/// <summary>
	///     The shared listener machinery - TCP accept loop, client registry, connect/disconnect lifecycle - independent
	///     of how a frame is parsed.
	/// </summary>
	/// <remarks>
	///     <see cref="ChatServerBase" /> (the chat protocol) and <see cref="RpcServer" /> (every Conan protocol
	///     listener) supply <c>CreateClient</c> and their <c>ReceivedPacket</c>, and each holds the concrete tracer its
	///     protocol traces both directions through. The receive half is protocol-typed, so it lives there rather than
	///     here.
	/// </remarks>
	public abstract class ServerBase : BackgroundService
	{
		protected readonly IConfiguration Configuration;
		protected readonly IDatabaseRepository Database;
		protected readonly ILogger Logger;

		// The live connections and the lookups over them, so "send to this character" need not be a listener method -
		// see ConnectionRegistry.
		//
		// Injected, not owned. Each listener needs its own registry (a chat connection is not a world connection), so
		// most subclasses pass `new ConnectionRegistry()` at their base call. The GameServer takes it from the
		// container instead, because its wire collaborators are container-resolved and would otherwise have to reach
		// back into the listener for this one field.
		protected readonly ConnectionRegistry Connections;
		private readonly TcpListener _tcpListener;

		public IPEndPoint LocalEndPoint => (IPEndPoint)_tcpListener.LocalEndpoint;
		protected IEnumerable<INetworkClient> NetworkClients => Connections.Clients;

		protected ServerBase(ushort port, ILogger logger, IConfiguration configuration, IDatabaseRepository database, ConnectionRegistry connections)
		{
			Connections = connections;
			Logger = logger;
			Configuration = configuration;
			Database = database;
			_tcpListener = new TcpListener(configuration.ServerListenAddress, port);
		}

		protected override async Task ExecuteAsync(CancellationToken cancellationToken)
		{
			try
			{
				_tcpListener.Start();
				cancellationToken.Register(_tcpListener.Stop);

				Logger.LogInformation("Listening on {Address}:{Port}.", LocalEndPoint.Address, LocalEndPoint.Port);
			}
			catch (Exception e)
			{
				// A bind failure must fail the host. Logging and returning would leave the process "running" with one
				// tier silently gone - a BackgroundService that returns early is simply absent - so a port conflict
				// would produce a server whose world listener never bound and that answers nothing.
				//
				// Rethrowing stops the host: .NET's default BackgroundServiceExceptionBehavior is StopHost, so an
				// unhandled ExecuteAsync exception brings the process down with this log line as the cause. There is no
				// useful degraded mode for a listener that cannot bind.
				Logger.LogCritical(e, "Failed to bind port {Port} - stopping the host. Another instance is probably already running.", LocalEndPoint.Port);
				throw;
			}

			while (!cancellationToken.IsCancellationRequested)
			{
				try
				{
					var socket = await _tcpListener.AcceptSocketAsync(cancellationToken);

					// Disable Nagle. The client frames the uncompressed world-entry burst per TCP segment, with no
					// reassembly, so it needs one packet per segment. With Nagle on, rapid small burst packets are
					// coalesced by ACK/CPU timing, which varies run to run, intermittently misframing the burst and
					// hanging the client on the loading screen. NoDelay flushes each send as its own segment, making
					// the segmentation deterministic.
					socket.NoDelay = true;

					var networkClient = CreateClient(socket);

					Connections.Add(networkClient);
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

		public IEnumerable<INetworkClientSession> GetWorldSessions()
		{
			return Connections.Sessions;
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
		// Default no-op; the GameServer overrides it to fire the create-editor post-morph burst deterministically.
		protected virtual Task ReceivedBatchComplete(INetworkClient client)
		{
			return Task.CompletedTask;
		}

		protected INetworkClient? ClientForAccount(uint accountId)
		{
			return Connections.ForAccount(accountId);
		}

		protected INetworkClient? ClientForCharacter(uint characterId)
		{
			return Connections.ForCharacter(characterId);
		}

		/// <summary>
		///     Builds the protocol-specific client for the accepted socket, wired with the shared lifecycle callbacks
		///     (<see cref="OnClientDisconnected" /> / <see cref="OnBatchComplete" />) plus its own receive dispatch.
		/// </summary>
		protected abstract NetworkClient CreateClient(Socket socket);

		// An extra opcode-specific body dump a handler emits on its own line, on the same byte-dump gate as the packet
		// lines. All line shaping lives in the logging layer.
		protected void LogPacketBodyDebug(string label, ReadOnlyMemory<byte> bytes)
		{
			Logger.LogBodyDump(label, bytes);
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

		// The shared disconnect/batch-drained callbacks a CreateClient wires onto its NetworkClient.
		protected async Task OnClientDisconnected(INetworkClient client)
		{
			try
			{
				Connections.Remove(client);
				await ClientDisconnected(client);
			}
			catch (Exception ex)
			{
				Logger.LogError(ex, "Exception in Disconnected");
			}
		}
	}

	/// <summary>
	///     The chat-protocol listener: each frame is parsed to a <see cref="ChatServerMessage" /> and dispatched.
	/// </summary>
	/// <remarks>
	///     The chat wire frame (<c>[u16 opcode][u16 bodyLen][body]</c>) has no Rpc sender/receiver/CRC model, so it
	///     keeps its own message type and framer rather than the structured Rpc path.
	/// </remarks>
	public abstract class ChatServerBase : ServerBase
	{
		// This channel's packet trace. The listener traces its receives through it and hands it to every client so the
		// sends are traced through the same object, with the same channel label.
		private readonly ChatPacketTracer _tracer;

		protected ChatServerBase(ushort port, ILogger logger, IConfiguration configuration, IDatabaseRepository database, ChatPacketTracer tracer)
			: base(port, logger, configuration, database, new ConnectionRegistry())
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

		// Inbound trace, through the same tracer the transport traces this channel's sends with: the method name and the
		// frame length both come off the parsed message, so the listener passes nothing but the packet.
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

	/// <summary>
	///     The structured-Rpc listener used by every Conan protocol tier: each frame is parsed straight to an
	///     <see cref="Rpc" /> and dispatched, with no per-frame packet object.
	/// </summary>
	public abstract class RpcServer : ServerBase
	{
		// This channel's packet trace - both directions through one object.
		private readonly ConanPacketTracer _tracer;

		protected RpcServer(ushort port, ILogger logger, IConfiguration configuration, IDatabaseRepository database, ConanPacketTracer tracer, ConnectionRegistry connections)
			: base(port, logger, configuration, database, connections)
		{
			_tracer = tracer;
		}

		protected override NetworkClient CreateClient(Socket socket)
		{
			return new RpcNetworkClient(socket, Logger, _tracer)
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

		// Inbound trace, through the same tracer the transport traces this channel's sends with: both the method name
		// (from the Receiver.Type + method pair, via ProtocolCatalog) and the FrameLength (total wire size, reported as
		// packetLength) come straight off the parsed Rpc, so the listener passes only the packet.
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
