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
			// Whether to host the in-process MCP server (Claude tooling) and the in-game Claude chat channel it
			// bridges. Defaults to enabled when unset; set "McpEnabled": false in faolan.json to disable both.
			public bool McpEnabled => !bool.TryParse(config["McpEnabled"], out var enabled) || enabled;

			public string DatabasePath => Path.GetFullPath(config["DatabasePath"]);

			public string ServerIpAddress => config["ServerIpAddress"];

			public string WelcomeString => config["WelcomeString"];

			public uint ServerIpAddressLong => BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse(config.ServerIpAddress).GetAddressBytes());

			public ushort AgentServerPort => Convert.ToUInt16(config["AgentServerPort"]);
			public ushort CsPlayerAgentPort => Convert.ToUInt16(config["CsPlayerAgentPort"]);

			public ushort McpPort => Convert.ToUInt16(config["McpPort"]);

			public ushort PlayerAgentPort => Convert.ToUInt16(config["PlayerAgentPort"]);
			public ushort UniverseAgentPort => Convert.ToUInt16(config["UniverseAgentPort"]);
		}
	}
}
