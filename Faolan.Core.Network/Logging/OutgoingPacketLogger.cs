using System;
using Faolan.Core.Network.Interfaces;
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

		protected abstract void LogPacket(byte[] bytes);
	}
}
