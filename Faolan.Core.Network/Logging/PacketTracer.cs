using System;
using Faolan.Core.Network.Interfaces;
using Faolan.Core.Network.Protocol;
using Microsoft.Extensions.Logging;

namespace Faolan.Core.Network.Logging
{
	// Shared scaffold for a channel's packet tracer: the ILogger, the channel label both directions tag their lines
	// with, and the try/catch guard - a malformed packet must never crash the send or receive path just to trace it.
	// Subclasses supply the per-protocol decode + render (through PacketLogFormatter, the one line shaper).
	//
	// Only the SEND hooks live here, because only sending is protocol-agnostic at the transport (NetworkClient traces
	// every frame it writes through IPacketTracer). The receive hooks are protocol-typed - an Rpc for the Conan tiers,
	// a ChatServerMessage for the chat tier - so each concrete tracer exposes its own, and its listener calls it.
	public abstract class PacketTracer : IPacketTracer
	{
		protected readonly ILogger Logger;

		public string ChannelName { get; }

		protected PacketTracer(ILogger logger, string channelName)
		{
			Logger = logger;
			ChannelName = channelName;
		}

		public void Transmitted(byte[] frame)
		{
			try
			{
				RenderTransmitted(frame);
			}
			catch (Exception ex)
			{
				RenderFailed(ex);
			}
		}

		public void Transmitted(in Rpc rpc)
		{
			try
			{
				RenderTransmitted(in rpc);
			}
			catch (Exception ex)
			{
				RenderFailed(ex);
			}
		}

		// The raw-frame TX render. The Conan tracer decodes the frame back to an Rpc and renders it through the SAME
		// path as a structured send (so a raw send can't produce a differently-shaped line); the chat tracer parses its
		// own frame format here - this is the only path it has, its wire has no Rpc model.
		protected abstract void RenderTransmitted(byte[] frame);

		// The structured TX render. Not abstract: the chat wire carries no Rpc, so that tracer never sees one.
		protected virtual void RenderTransmitted(in Rpc rpc)
		{
		}

		// A trace must never take down the path it is tracing - a render fault is reported and swallowed.
		protected void RenderFailed(Exception ex)
		{
			Logger.LogDebug(ex, "Failed to render a {Channel} packet trace.", ChannelName);
		}
	}
}
