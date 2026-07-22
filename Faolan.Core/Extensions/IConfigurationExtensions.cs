using System;
using Faolan.Core.Enums;
using Microsoft.Extensions.Configuration;

namespace Faolan.Core.Extensions
{
	// ReSharper disable once InconsistentNaming
	public static class IConfigurationExtensions
	{
		extension(IConfiguration config)
		{
			public bool McpEnabled => bool.TryParse(config["McpEnabled"], out var enabled) && enabled;
			public FaolanDbProvider DatabaseProvider => Enum.Parse<FaolanDbProvider>(config["DatabaseProvider"]!, true);
			public string DatabaseConnection => config["DatabaseConnectionString"]!;
			public string ServerIpAddress => config["ServerIpAddress"]!;
			public string WelcomeString => config["WelcomeString"]!;
			public ushort McpPort => Convert.ToUInt16(config["McpPort"]);
			public ushort PlayerAgentPort => Convert.ToUInt16(config["PlayerAgentPort"]);
			public ushort UniverseAgentPort => Convert.ToUInt16(config["UniverseAgentPort"]);
		}
	}
}
