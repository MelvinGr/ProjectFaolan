using System;
using Faolan.Core.Network.Interfaces;
using Faolan.Core.Network.Protocol;
using Microsoft.Extensions.Logging;

namespace Faolan.Core.Network.Logging
{
	// Shared scaffold for the server -> client (DL) packet loggers: the send-path hook (IPacketLogger.Log(byte[])) with
	// the decode wrapped in a try/catch, so a malformed packet can never crash the send path just to trace it.
	// Subclasses implement the per-protocol decode (LogPacket) and render through PacketLogFormatter. ChannelName is
	// exposed so the inbound path (Server) can label its UL lines with the same channel without re-plumbing it through
	// every listener - the listener already gave its outbound logger the channel name.
	public abstract class OutgoingPacketLogger : IPacketLogger
	{
		protected readonly ILogger Logger;

		public string ChannelName { get; }

		protected OutgoingPacketLogger(ILogger logger, string channelName)
		{
			Logger = logger;
			ChannelName = channelName;
		}

		public void Log(byte[] bytes)
		{
			try
			{
				LogPacket(bytes);
			}
			catch (Exception ex)
			{
				Logger.LogDebug(ex, "Failed to decode outgoing {Channel} packet for logging.", ChannelName);
			}
		}

		// The structured send-path log (Gitea #406 phase 6). Default renders the SAME way as Log(byte[]) - by
		// serializing-and-re-parsing - for loggers that don't model the Conan RPC header (the chat logger). The Conan
		// logger overrides LogPacket(in Rpc, ...) to skip the re-parse and render from the Rpc fields directly; the
		// try/catch lives here so a render failure can never crash the send path just to trace it.
		public void LogRpc(in Rpc rpc, byte[] wireBytes)
		{
			try
			{
				LogPacket(in rpc, wireBytes);
			}
			catch (Exception ex)
			{
				Logger.LogDebug(ex, "Failed to render outgoing {Channel} Rpc for logging.", ChannelName);
			}
		}

		protected abstract void LogPacket(byte[] bytes);

		// Default: fall back to the byte[] decode path. ConanOutgoingPacketLogger overrides this to render from the Rpc.
		protected virtual void LogPacket(in Rpc rpc, byte[] wireBytes)
		{
			LogPacket(wireBytes);
		}
	}
}
