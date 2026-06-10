using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using Microsoft.Extensions.Configuration;

namespace Faolan.Core.Extensions
{
	// ReSharper disable once InconsistentNaming
	public static class IConfigurationExtensions
	{
		extension(IConfiguration config)
		{
			public string DatabasePath =>
				Path.GetFullPath(config[Statics.IsDebug
					? "DatabasePathDev"
					: "DatabasePath"]);

			public string ServerIpAddress => config["ServerIpAddress"];

			public string WelcomeString => config["WelcomeString"];

			public uint ServerIpAddressLong =>
				BinaryPrimitives.ReadUInt32BigEndian(IPAddress
					.Parse(config.ServerIpAddress)
					.GetAddressBytes());

			public ushort AgentServerPort => Convert.ToUInt16(config["AgentServerPort"]);
			public ushort CsPlayerAgentPort => Convert.ToUInt16(config["CsPlayerAgentPort"]);
			public ushort PlayerAgentPort => Convert.ToUInt16(config["PlayerAgentPort"]);

			public ushort UniverseAgentPort => Convert.ToUInt16(config["UniverseAgentPort"]);
		}
	}
}
