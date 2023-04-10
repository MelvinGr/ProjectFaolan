using System;
using System.IO;
using System.Net;
using Faolan.Core;
using Microsoft.Extensions.Configuration;

namespace Faolan.Extensions
{
	// ReSharper disable once InconsistentNaming
	public static class IConfigurationExtensions
	{
		public static string ServerIpAddress(this IConfiguration config)
		{
			return config["ServerIpAddress"];
		}

		public static long ServerIpAddressLong(this IConfiguration config)
		{
#pragma warning disable 618
			return IPAddress.HostToNetworkOrder(IPAddress.TryParse(config.ServerIpAddress(), out var ip) ? ip.Address : throw new Exception("!IPAddress.TryParse"));
#pragma warning restore 618
		}

		public static ushort UniverseAgentPort(this IConfiguration config)
		{
			return ushort.Parse(config["UniverseAgentPort"]);
		}

		public static ushort PlayerAgentPort(this IConfiguration config)
		{
			return ushort.Parse(config["PlayerAgentPort"]);
		}

		public static ushort CsPlayerAgentPort(this IConfiguration config)
		{
			return ushort.Parse(config["CsPlayerAgentPort"]);
		}

		public static ushort AgentServerPort(this IConfiguration config)
		{
			return ushort.Parse(config["AgentServerPort"]);
		}

		public static string DatabasePath(this IConfiguration config)
		{
			return Path.GetFullPath(Statics.IsDebug ? config["DatabasePathDev"] : config["DatabasePath"]);
		}

		public static string WelcomeString(this IConfiguration config)
		{
			return config["WelcomeString"].Replace("$BUILDSTRING", Statics.BuildString);
		}
	}
}
