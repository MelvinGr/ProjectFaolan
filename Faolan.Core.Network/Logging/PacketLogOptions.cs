using System;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Logging
{
	// Global packet-trace logging switches. Each in/outbound packet is now logged on a SINGLE line; the raw
	// byte dump (headerData/body hex) is folded onto that same line rather than emitted as separate lines.
	// Set from faolan.json Log:PacketBytes at startup (see Program.cs).
	public static class PacketLogOptions
	{
		// When false, the raw byte dump is suppressed on every packet line even at Debug level (leaving just the
		// opcode + length summary). Default true preserves the historical Debug-level byte trace.
		public static bool ShouldLogBytes { get; set; } = true;

		// The " label={ .. } .." suffix appended to a packet's single log line, or "" when bytes are suppressed.
		// Each (label, data) pair with data is rendered as " <label>({len})={hex}".
		//
		// A segment is a byte WINDOW (ReadOnlyMemory<byte>), not an array: both directions hold their headerData/body as
		// memory over the frame, so a suppressed trace (the common case) costs no copy at all - the hex is materialized
		// only past the ShouldLogBytes gate.
		public static string BytesSuffix(params (string Label, ReadOnlyMemory<byte> Data)[] segments)
		{
			if (!ShouldLogBytes)
				return "";

			var suffix = "";
			foreach (var (label, data) in segments)
			{
				if (data.Length > 0)
					suffix += $" {label}({data.Length})={data.ToHex()}";
			}

			return suffix;
		}
	}
}
