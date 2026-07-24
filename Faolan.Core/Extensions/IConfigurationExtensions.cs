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
			// Required values. faolan.json is OPTIONAL now (a container can be configured purely by env vars), so a
			// missing key must fail with a CLEAR message rather than an NRE (config[key]!) / ArgumentNullException
			// (Enum.Parse(null)) / silent 0 (Convert.ToUInt16(null)).
			public FaolanDbProvider DatabaseProvider => Enum.Parse<FaolanDbProvider>(Required(config, "Database:Provider"), true);

			// Optional toggles - default off when unset. The grouped keys use the "Section:Key" path (nested in
			// faolan.json; env override with the __ separator, e.g. Log__PacketBytes / Mcp__Enabled).
			public bool LogPacketBytes => bool.TryParse(config["Log:PacketBytes"], out var enabled) && enabled;
			public bool McpEnabled => bool.TryParse(config["Mcp:Enabled"], out var enabled) && enabled;

			public double ChannelBacklogGraceMs => double.TryParse(config["ChatBridge:BacklogGraceMs"], out var ms)
				? ms
				: 120_000;

			public double ChannelPollMs => double.TryParse(config["ChatBridge:PollMs"], out var ms)
				? ms
				: 1500;

			public string CdnMirrorIp => config["CdnMirror:Ip"] ?? "127.0.0.1";

			// CDN content mirror (Faolan.Web.Content). All optional with the built-in defaults so the mirror runs with no
			// config at all; override via faolan.json, env vars, or the --store/--ip/--port switches (mapped to these keys).
			public string CdnMirrorStore => config["CdnMirror:Store"] ?? "mirror-store";
			public string DatabaseConnection => Required(config, "Database:ConnectionString");

			// Log file location. Optional - defaults to faolan.log in the CWD; point it at a mounted volume (container)
			// or a per-run path via env/cmdline/json like any other config value.
			public string LogFile => config["Log:File"] ?? "faolan.log";

			// Faolan.Mcp chat-bridge (the stdio "faolan-chat" MCP server). All optional with the built-in defaults so
			// the bridge runs with no config; McpUrl is the game-server MCP endpoint it connects to, the two Channel*
			// values tune its poll loop (milliseconds).
			public string McpUrl => config["Mcp:Url"] ?? "http://127.0.0.1:7099/mcp";
			public string ServerIpAddress => Required(config, "ServerIpAddress");
			public string WelcomeString => Required(config, "WelcomeString");

			public ushort CdnMirrorPort => ushort.TryParse(config["CdnMirror:Port"], out var port)
				? port
				: (ushort)7081;

			public ushort McpPort => ushort.Parse(Required(config, "Mcp:Port"));
			public ushort PlayerAgentPort => ushort.Parse(Required(config, "PlayerAgentPort"));
			public ushort UniverseAgentPort => ushort.Parse(Required(config, "UniverseAgentPort"));
		}

		private static string Required(IConfiguration config, string key)
		{
			return config[key] ?? throw new InvalidOperationException($"Missing required configuration '{key}' - set it in faolan.json or as an environment variable.");
		}
	}
}
