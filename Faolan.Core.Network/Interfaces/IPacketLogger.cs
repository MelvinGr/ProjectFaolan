using Faolan.Core.Network.Protocol;

namespace Faolan.Core.Network.Interfaces
{
	public interface IPacketLogger
	{
		// The channel label ("GameServer", "ChatServer", …) this logger tags its TX trace lines with, and which the
		// Server reuses for the matching inbound (RX) lines so both directions read "[Channel …]". "" when untagged
		// (the null logger). Lets the inbound path read the channel without downcasting to OutgoingPacketLogger.
		string ChannelName => "";

		// Log a raw server->client (TX) frame from its bytes. Used by protocols that hand the transport bytes (the chat
		// server) and by any residual raw send - the logger has to parse the bytes to trace them.
		void Log(byte[] bytes);

		// Log a server->client (TX) frame from the structured Rpc that produced it (Gitea #406 phase 6): the trace is
		// rendered from the message's own Opcode/Receiver/HeaderData/Body with NO re-parse. `frameByteLength` is the
		// serialized frame's length (known for free at the send site) - the only thing the trace needs that the Rpc
		// itself doesn't carry.
		void Log(in Rpc rpc, int frameByteLength);
	}

	// The "no logging" IPacketLogger (the default when a listener wires up no trace).
	public sealed class NullPacketLogger : IPacketLogger
	{
		public static readonly NullPacketLogger Instance = new();

		private NullPacketLogger()
		{
		}

		public void Log(byte[] bytes)
		{
		}

		public void Log(in Rpc rpc, int frameByteLength)
		{
		}
	}
}
