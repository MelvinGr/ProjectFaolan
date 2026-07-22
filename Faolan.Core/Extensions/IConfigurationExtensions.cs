using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using Faolan.Core.Enums;
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

			public string DatabasePath => Path.GetFullPath(Required(config, "DatabasePath"));

			// The database backend: "Sqlite" (default) or "Postgres". Set "DatabaseProvider": "Postgres" in
			// faolan.json to run against Postgres instead of the committed faolan.db seed (#379).
			public FaolanDbProvider DatabaseProvider => Enum.Parse<FaolanDbProvider>(Required(config, "DatabaseProvider"), true);

			// The connection string to hand the EF provider. Postgres uses the required "DatabaseConnectionString"
			// (a full Npgsql string); SQLite derives `Data Source=<DatabasePath>` from the existing key, so the
			// default single-key faolan.json keeps working unchanged.
			public string DatabaseConnection => config.DatabaseProvider switch
			{
				FaolanDbProvider.Postgres => Required(config, "DatabaseConnectionString"),
				FaolanDbProvider.Sqlite => $"Data Source={config.DatabasePath}",
				_ => throw new ArgumentOutOfRangeException(nameof(config), config.DatabaseProvider, "Unknown database provider.")
			};

			public string ServerIpAddress => Required(config, "ServerIpAddress");

			public string WelcomeString => Required(config, "WelcomeString");

			public uint ServerIpAddressLong => BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse(config.ServerIpAddress).GetAddressBytes());

			// #324: the realm-local service ports (world/chat/guild) live on the Realm row, not global config - the
			// realm host binds them from its Realm row. Only the shared login tier's ports stay in config below.
			public ushort McpPort => Convert.ToUInt16(config["McpPort"]);

			public ushort PlayerAgentPort => Convert.ToUInt16(config["PlayerAgentPort"]);
			public ushort UniverseAgentPort => Convert.ToUInt16(config["UniverseAgentPort"]);
		}

		// A required config value: surface a clear error if it is missing rather than passing a null downstream.
		private static string Required(IConfiguration config, string key)
		{
			return config[key] ?? throw new InvalidOperationException($"Missing required configuration value '{key}'.");
		}
	}
}
