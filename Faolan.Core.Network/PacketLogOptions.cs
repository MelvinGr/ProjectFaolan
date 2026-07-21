using Faolan.Core.Extensions;
using Microsoft.Extensions.Logging;

namespace Faolan.Core.Network
{
	// Global packet-trace logging switches. Each in/outbound packet is now logged on a SINGLE line; the raw
	// byte dump (headerData/body hex) is folded onto that same line rather than emitted as separate lines.
	// Set from faolan.json "LogPacketBytes" at startup (see Program.cs).
	public static class PacketLogOptions
	{
		// When false, the raw byte dump is suppressed on every packet line even at Debug level (leaving just the
		// opcode + length summary). Default true preserves the historical Debug-level byte trace.
		public static bool IncludeBytes = true;

		// The " label={ .. } .." suffix appended to a packet's single log line, or "" when bytes are suppressed.
		// Each (label, data) pair with data is rendered as " <label>({len})={hex}".
		public static string BytesSuffix(ILogger logger, params (string Label, byte[] Data)[] segments)
		{
			if (!ShouldLogBytes(logger))
				return "";

			var suffix = "";
			foreach (var (label, data) in segments)
			{
				if (data?.Length > 0)
					suffix += $" {label}({data.Length})={data.ToHex()}";
			}

			return suffix;
		}

		// True when a byte dump should be appended: globally enabled AND the logger is at Debug verbosity.
		public static bool ShouldLogBytes(ILogger logger)
		{
			return IncludeBytes && logger.IsEnabled(LogLevel.Debug);
		}
	}
}
