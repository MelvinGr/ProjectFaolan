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

			// Account "premium" (subscribed) advertisement in the PlayerAgent character list (0x20EF). Decoded by
			// diffing the account-login-{nopremium,premium} captures: the only per-account difference in the trailing
			// block is the subscribed flag byte 0x81 -> 0x80 and the unlocked-character-slot count 4 -> 10. Defaults to
			// ON (unlike the toggles above) - the historical behaviour is that every account is treated as subscribed.
			public bool AccountPremium => !bool.TryParse(config["Account:Premium"], out var premium) || premium;

			// Unlocked character-slot counts advertised in that same block, per subscription state.
			public uint AccountPremiumCharSlots => uint.TryParse(config["Account:PremiumCharSlots"], out var slots) ? slots : 10;
			public uint AccountFreeCharSlots => uint.TryParse(config["Account:FreeCharSlots"], out var slots) ? slots : 4;

			// Whether a client whose login block the server CANNOT decrypt may still log in. That block only decrypts
			// for a client patched with our DH public key (Faolan.ClientLauncher --patch-login), so an unpatched retail
			// client is authenticated on the account NAME alone - it never proves the password. Defaults to ON, the
			// historical behaviour: turning password validation on must not lock out the clients that predate it.
			// Turn it OFF (Login:AllowUnpatchedClients = false) for a deployment that hands out patched clients and
			// wants the password to actually be required.
			public bool LoginAllowUnpatchedClients => !bool.TryParse(config["Login:AllowUnpatchedClients"], out var allow) || allow;

			public double ChannelBacklogGraceMs => double.TryParse(config["ChatBridge:BacklogGraceMs"], out var ms)
				? ms
				: 120_000;

			public double ChannelPollMs => double.TryParse(config["ChatBridge:PollMs"], out var ms)
				? ms
				: 1500;

			public string CdnMirrorIp => config["CdnMirror:Ip"] ?? "127.0.0.1";

			// The client's RDB folder - part of the game INSTALL, not this repo, so it has no default. One key for
			// every consumer (Faolan.Web's viewer and the importer's rdb-* modules/reports) and every input method:
			// `RdbDir` in faolan.json, `RdbDir` in the environment, `--RdbDir <dir>` on the command line.
			public string? RdbDir => config["RdbDir"];

			// CDN content mirror (Faolan.Web.Content). All optional with the built-in defaults so the mirror runs with no
			// config at all; override in faolan.json, the environment, or on the command line - all under this one name.
			public string CdnMirrorStore => config["CdnMirror:Store"] ?? "mirror-store";
			public string DatabaseConnection => Required(config, "Database:ConnectionString");

			// Log file location. Optional - defaults to faolan.log in the CWD; point it at a mounted volume (container)
			// or a per-run path via env/cmdline/json like any other config value.
			public string LogFile => config["Log:File"] ?? "faolan.log";

			// Faolan.Mcp chat-bridge (the stdio "faolan-chat" MCP server). All optional with the built-in defaults so
			// the bridge runs with no config; McpUrl is the game-server MCP endpoint it connects to, the two Channel*
			// values tune its poll loop (milliseconds).
			public string McpUrl => config["Mcp:Url"] ?? "http://127.0.0.1:7099/mcp";
			// The client build the protocol was decoded against - every capture in the corpus is this one. A client
			// reporting anything else is not refused.
			public string ClientVersion => config["ClientVersion"] ?? "v5.01.0@602248";
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
