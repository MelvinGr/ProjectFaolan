using System;
using System.Collections.Generic;
using System.Net;

namespace Faolan.PacketAnalyzer
{
	public static class ReverseDnsResolver
	{
		private static readonly Dictionary<string, string> HostNames = new Dictionary<string, string>();

		public static string? GetHostName(IPAddress ipAddress)
		{
			try
			{
				var ip = ipAddress.ToString();
				var isLocal = IsLocalAddress(ipAddress);

				if (isLocal)
					HostNames[ip] = "localhost";
				else if (!HostNames.ContainsKey(ip) || string.IsNullOrEmpty(HostNames[ip]))
				{
					var hostName = Dns.GetHostEntry(ip).HostName;
					HostNames[ip] = string.Equals(hostName, "localhost", StringComparison.OrdinalIgnoreCase) || string.Equals(hostName, ip, StringComparison.OrdinalIgnoreCase)
						? ip
						: hostName;
				}

				return HostNames[ip];
			}
			catch //(Exception e)
			{
				return null;
			}
		}

		public static bool IsLocalAddress(IPAddress ipAddress)
		{
			if (IPAddress.IsLoopback(ipAddress))
				return true;

			var bytes = ipAddress.GetAddressBytes();
			if (bytes.Length != 4)
				return false;

			return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) || (bytes[0] == 192 && bytes[1] == 168);
		}
	}
}
