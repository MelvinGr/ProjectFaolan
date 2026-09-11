using Faolan.Core.Network.Protocol;

namespace Faolan.Core.Network.Interfaces
{
	/// <summary>
	///     The packet trace for one protocol channel. A channel has exactly one tracer serving both directions; this is
	///     the half the transport needs: the network client traces every send through it.
	/// </summary>
	/// <remarks>
	///     The receive half is protocol-typed and lives on the concrete tracer the listener holds
	///     (<c>ConanPacketTracer.Received</c> / <c>ChatPacketTracer.Received</c>). Keeping both directions on one object
	///     is what makes an RX line and a TX line agree on the channel label, the opcode naming and the byte-dump gate.
	/// </remarks>
	public interface IPacketTracer
	{
		/// <summary>
		///     The channel label ("GameServer", "ChatServer", …) every line of this channel is tagged with, in both
		///     directions. Empty when untagged (the null tracer).
		/// </summary>
		string ChannelName => "";

		/// <summary>
		///     Traces a server-to-client frame from its raw bytes, for protocols that hand the transport bytes (the chat
		///     server) and for any residual raw send. The tracer decodes the frame itself to render it.
		/// </summary>
		void Transmitted(byte[] frame);

		/// <summary>
		///     Traces a server-to-client frame from the structured <see cref="Rpc" /> that produced it, rendered from
		///     the message's own Method/Receiver/Body with no re-parse.
		/// </summary>
		/// <remarks>
		///     The transport stamps <c>FrameLength</c> before tracing, so the whole line comes off the message, as it
		///     does for an inbound one the parser stamped.
		/// </remarks>
		void Transmitted(in Rpc rpc);
	}
}
