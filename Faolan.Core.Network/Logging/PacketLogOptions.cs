using System;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Logging
{
	/// <summary>
	///     Global packet-trace logging switches. Each inbound or outbound packet is logged on a single line, with the
	///     raw byte dump (the body hex) folded onto that same line. Set from faolan.json <c>Log:PacketBytes</c> at
	///     startup.
	/// </summary>
	public static class PacketLogOptions
	{
		/// <summary>
		///     When false, the raw byte dump is suppressed on every packet line even at Debug level, leaving just the
		///     opcode and length summary.
		/// </summary>
		public static bool ShouldLogBytes { get; set; } = true;

		/// <summary>
		///     The <c>" body({len})={hex}"</c> suffix appended to a packet's single log line, or an empty string when
		///     bytes are suppressed.
		/// </summary>
		/// <remarks>
		///     The body is a byte window rather than an array: both directions hold it as memory over the frame, so a
		///     suppressed trace - the common case - costs no copy, and the hex is materialized only past the
		///     <see cref="ShouldLogBytes" /> gate.
		/// </remarks>
		public static string BodySuffix(ReadOnlyMemory<byte> body)
		{
			return !ShouldLogBytes || body.Length == 0
				? ""
				: $" body({body.Length})={body.ToHex()}";
		}
	}
}
