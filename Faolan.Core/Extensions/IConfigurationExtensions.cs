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
			// Optional toggles - default off when unset.
			public bool LogPacketBytes => bool.TryParse(config["LogPacketBytes"], out var enabled) && enabled;
			public bool McpEnabled => bool.TryParse(config["McpEnabled"], out var enabled) && enabled;

			// Log file location. Optional - defaults to faolan.log in the CWD; point it at a mounted volume (container)
			// or a per-run path via env/cmdline/json like any other config value.
			public string LogFile => config["LogFile"] ?? "faolan.log";

			// Required values. faolan.json is OPTIONAL now (a container can be configured purely by env vars), so a
			// missing key must fail with a CLEAR message rather than an NRE (config[key]!) / ArgumentNullException
			// (Enum.Parse(null)) / silent 0 (Convert.ToUInt16(null)).
			public FaolanDbProvider DatabaseProvider => Enum.Parse<FaolanDbProvider>(Required(config, "DatabaseProvider"), true);
			public string DatabaseConnection => Required(config, "DatabaseConnectionString");
			public string ServerIpAddress => Required(config, "ServerIpAddress");
			public string WelcomeString => Required(config, "WelcomeString");
			public ushort McpPort => ushort.Parse(Required(config, "McpPort"));
			public ushort PlayerAgentPort => ushort.Parse(Required(config, "PlayerAgentPort"));
			public ushort UniverseAgentPort => ushort.Parse(Required(config, "UniverseAgentPort"));
		}

		private static string Required(IConfiguration config, string key) =>
			config[key] ?? throw new InvalidOperationException($"Missing required configuration '{key}' - set it in faolan.json or as an environment variable.");
	}
}
