using System;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Logging
{
	// Global packet-trace logging switches. Each in/outbound packet is now logged on a SINGLE line; the raw
	// byte dump (the body hex) is folded onto that same line rather than emitted as separate lines.
	// Set from faolan.json Log:PacketBytes at startup (see Program.cs).
	public static class PacketLogOptions
	{
		// When false, the raw byte dump is suppressed on every packet line even at Debug level (leaving just the
		// opcode + length summary). Default true preserves the historical Debug-level byte trace.
		public static bool ShouldLogBytes { get; set; } = true;

		// The " body({len})={hex}" suffix appended to a packet's single log line, or "" when bytes are suppressed.
		//
		// This took a params array of labelled segments while a packet had TWO things worth dumping - the body and the
		// header's `headerData` tail. #610 established that the tail was never a field (it is the back half of the
		// method varint), so there is exactly one segment left and the labelling machinery went with it - which also
		// retired the CS8620 tuple-inference suppression each tracer needed to call through it.
		//
		// The body is a byte WINDOW (ReadOnlyMemory<byte>), not an array: both directions hold it as memory over the
		// frame, so a suppressed trace (the common case) costs no copy at all - the hex is materialized only past the
		// ShouldLogBytes gate.
		public static string BodySuffix(ReadOnlyMemory<byte> body)
		{
			return !ShouldLogBytes || body.Length == 0
				? ""
				: $" body({body.Length})={body.ToHex()}";
		}
	}
}
