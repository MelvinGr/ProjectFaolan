using System;
using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Faolan.PacketAnalyzer
{
	internal static class PacketAdvertisedEndpointParser
	{
		public static bool TryExtractEndpointPort(byte[] payload, out ushort port)
		{
			return TryExtractStringEndpointPort(payload, out port) || TryExtractBinaryEndpointPort(payload, out port);
		}

		public static bool TryFindHostPort(byte[] bytes, out ushort port)
		{
			port = 0;

			var ascii = Encoding.ASCII.GetString(bytes);
			var separatorIndex = ascii.IndexOf(':');
			while (separatorIndex >= 0)
			{
				var hostStart = separatorIndex - 1;
				while (hostStart >= 0 && IsHostCharacter(ascii[hostStart]))
					hostStart--;

				hostStart++;

				var portStart = separatorIndex + 1;
				var portEnd = portStart;
				while (portEnd < ascii.Length && char.IsDigit(ascii[portEnd]) && portEnd - portStart < 5)
					portEnd++;

				var hostLength = separatorIndex - hostStart;
				var portLength = portEnd - portStart;
				if (hostLength > 0 && portLength is > 1 and <= 5)
				{
					var candidatePortText = ascii.Substring(portStart, portLength);
					for (var candidateLength = candidatePortText.Length; candidateLength >= 2; candidateLength--)
					{
						if (ushort.TryParse(candidatePortText[..candidateLength], out port))
							return true;
					}
				}

				separatorIndex = ascii.IndexOf(':', separatorIndex + 1);
			}

			return false;
		}

		private static bool IsHostCharacter(char value)
		{
			return char.IsLetterOrDigit(value) || value is '.' or '-';
		}

		private static bool TryExtractBinaryEndpointPort(byte[] payload, out ushort port)
		{
			port = 0;

			if (payload.Length < sizeof(uint) + sizeof(ushort))
				return false;

			var span = payload.AsSpan();
			var ipBytes = span[..sizeof(uint)].ToArray();
			port = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(sizeof(uint), sizeof(ushort)));
			if (port == 0)
				return false;

			return !IPAddress.Any.Equals(new IPAddress(ipBytes));
		}

		private static bool TryExtractStringEndpointPort(byte[] payload, out ushort port)
		{
			port = 0;

			if (payload.Length < sizeof(ushort))
				return false;

			var stringLength = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(0, sizeof(ushort)));
			if (stringLength == 0 || stringLength > payload.Length - sizeof(ushort))
				return false;

			var candidate = Encoding.ASCII.GetString(payload, sizeof(ushort), stringLength);
			var separatorIndex = candidate.LastIndexOf(':');
			if (separatorIndex <= 0 || separatorIndex >= candidate.Length - 1)
				return false;

			return ushort.TryParse(candidate[(separatorIndex + 1)..], out port);
		}
	}
}
