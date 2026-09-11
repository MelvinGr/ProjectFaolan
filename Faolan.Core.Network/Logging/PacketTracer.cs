using System;
using Faolan.Core.Network.Interfaces;
using Faolan.Core.Network.Protocol;
using Microsoft.Extensions.Logging;

namespace Faolan.Core.Network.Logging
{
	/// <summary>
	///     Shared scaffold for a channel's packet tracer: the logger, the channel label both directions tag their lines
	///     with, and the try/catch guard that keeps a malformed packet from crashing the send or receive path merely to
	///     trace it. Subclasses supply the per-protocol decode and render, through
	///     <see cref="PacketLogFormatter" />.
	/// </summary>
	/// <remarks>
	///     Only the send hooks live here, because only sending is protocol-agnostic at the transport - the transport
	///     traces every frame it writes through <see cref="IPacketTracer" />. The receive hooks are protocol-typed (an
	///     <see cref="Rpc" /> for the Conan tiers, a <c>ChatServerMessage</c> for the chat tier), so each concrete
	///     tracer exposes its own and its listener calls it.
	/// </remarks>
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

		// The structured transmit render. Not abstract: the chat wire carries no Rpc, so that tracer never sees one.
		protected virtual void RenderTransmitted(in Rpc rpc)
		{
		}

		// A trace must never take down the path it is tracing - a render fault is reported and swallowed.
		protected void RenderFailed(Exception ex)
		{
			Logger.LogDebug(ex, "Failed to render a {Channel} packet trace.", ChannelName);
		}

		// The raw-frame transmit render. The Conan tracer decodes the frame back to an Rpc and renders it through the
		// same path as a structured send, so a raw send cannot produce a differently-shaped line; the chat tracer parses
		// its own frame format here, which is the only path it has because its wire has no Rpc model.
		protected abstract void RenderTransmitted(byte[] frame);
	}
}
