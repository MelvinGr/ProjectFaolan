using Faolan.Core.Network.Protocol;

namespace Faolan.Core.Network.Interfaces
{
	public interface IPacketLogger
	{
		// The channel label ("GameServer", "ChatServer", …) this logger tags its DL trace lines with, and which the
		// Server reuses for the matching inbound (UL) lines so both directions read "[Channel …]". "" when untagged
		// (the null logger). Lets the inbound path read the channel without downcasting to OutgoingPacketLogger.
		string ChannelName => "";

		void Log(byte[] bytes);

		// Structured server->client (DL) log for an Rpc send (Gitea #406 phase 6): render the trace directly from the
		// message model instead of re-parsing the serialized bytes back into a packet. `wireBytes` is the already-
		// serialized frame (so the log can report the total wire length). The default falls back to the byte[] path for
		// loggers that don't model the Conan RPC header (the chat logger, the null logger); ConanOutgoingPacketLogger
		// overrides it to render from the Rpc's own Sender/Receiver/Opcode/HeaderData/Body.
		void LogRpc(in Rpc rpc, byte[] wireBytes)
		{
			Log(wireBytes);
		}
	}

	// The "no logging" IPacketLogger (the default when a listener wires up no trace). LogRpc uses the interface
	// default, which routes to Log - a no-op here - so it needs no override.
	public sealed class NullPacketLogger : IPacketLogger
	{
		public static readonly NullPacketLogger Instance = new();

		private NullPacketLogger()
		{
		}

		public void Log(byte[] bytes)
		{
		}
	}
}
