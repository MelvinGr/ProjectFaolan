using Faolan.Core.Network.Protocol;

namespace Faolan.Core.Network.Interfaces
{
	// The packet TRACE for one protocol channel. A channel has exactly ONE tracer and it serves BOTH directions - this
	// is the half the TRANSPORT needs (NetworkClient traces every send through it); the receive half is protocol-typed
	// and lives on the concrete tracer, which the listener holds (ConanPacketTracer.Received / ChatPacketTracer.Received).
	// Keeping both directions on one object is what makes an RX line and a TX line agree on the channel label, the
	// opcode naming and the byte-dump gate instead of each direction shaping its own (Gitea #406 phase 6).
	public interface IPacketTracer
	{
		// The channel label ("GameServer", "ChatServer", …) every line of this channel is tagged with, in both
		// directions. "" when untagged (the null tracer).
		string ChannelName => "";

		// Trace a server->client (TX) frame from its raw bytes - for protocols that hand the transport bytes (the chat
		// server) and for any residual raw send. The tracer has to decode the frame itself to render it.
		void Transmitted(byte[] frame);

		// Trace a server->client (TX) frame from the structured Rpc that produced it (Gitea #406 phase 6): rendered from
		// the message's own Opcode/Receiver/HeaderData/Body with NO re-parse. The transport stamps FrameLength before
		// tracing, so the whole line comes off the message - the same as an inbound one, which the parser stamped.
		void Transmitted(in Rpc rpc);
	}
}
