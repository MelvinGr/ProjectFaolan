using System.Collections.Generic;
using System.Net;

namespace Faolan.PacketAnalyzer
{
	public static class ReverseDnsResolver
	{
		private static readonly Dictionary<string, string> HostNames = new();

		public static string GetHostName(IPAddress ipAddress)
		{
			try
			{
				var ip = ipAddress.ToString();

				if (ip.StartsWith("192.168."))
					HostNames[ip] = "localhost";
				else if (!HostNames.ContainsKey(ip) || string.IsNullOrEmpty(HostNames[ip]))
					HostNames[ip] = Dns.GetHostEntry(ip).HostName;

				return HostNames[ip];
			}
			catch //(Exception e)
			{
				return null;
			}
		}
	}
}
