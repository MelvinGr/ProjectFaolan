using System;
using Faolan.Core.Enums;
using Microsoft.Extensions.Configuration;

namespace Faolan.Core.Extensions
{
	// The TYPED reading of a configuration value - and only that.
	//
	// ⚠️ The fallbacks used to live here, one `?? "faolan.log"` / `TryParse ? v : 10` per accessor, while the option
	// DECLARATION in a *Command.cs carried the same setting's name and description. Two lists of one thing, kept in
	// step by hand: `--help` could describe a default the code did not use, and three settings (ClientVersion and the
	// two ChatBridge ones) had a fallback here and no declaration anywhere, so no --help mentioned them.
	//
	// They now live in FaolanSettings, whose DEFAULTS layer sits at the bottom of the configuration stack
	// (FaolanCommandLine.Build). So a value is always present, and these accessors do one job: parse it.
	//
	// ⚠️ Which makes a MALFORMED value throw where it used to be swallowed. That is deliberate and matches the rest
	// of the repo (DhKeys.Configure rejects junk at startup): `Account:PremiumCharSlots = ten` used to silently mean
	// 10, which is a setting that reads as applied and is not.
	// ReSharper disable once InconsistentNaming
	public static class IConfigurationExtensions
	{
		extension(IConfiguration config)
		{
			// Required values. faolan.json is OPTIONAL now (a container can be configured purely by env vars), so a
			// missing key must fail with a CLEAR message rather than an NRE (config[key]!) / ArgumentNullException
			// (Enum.Parse(null)) / silent 0 (Convert.ToUInt16(null)).
			public FaolanDbProvider DatabaseProvider => Enum.Parse<FaolanDbProvider>(Required(config, "Database:Provider"), true);

			// The grouped keys use the "Section:Key" path (nested in faolan.json; env override with the __ separator,
			// e.g. Log__PacketBytes / Mcp__Enabled).
			public bool LogPacketBytes => Bool(config, "Log:PacketBytes");
			public bool McpEnabled => Bool(config, "Mcp:Enabled");

			// Account "premium" (subscribed) advertisement in the PlayerAgent character list (0x20EF). Decoded by
			// diffing the account-login-{nopremium,premium} captures: the only per-account difference in the trailing
			// block is the subscribed flag byte 0x81 -> 0x80 and the unlocked-character-slot count 4 -> 10.
			public bool AccountPremium => Bool(config, "Account:Premium");

			// Unlocked character-slot counts advertised in that same block, per subscription state.
			public uint AccountPremiumCharSlots => UInt(config, "Account:PremiumCharSlots");
			public uint AccountFreeCharSlots => UInt(config, "Account:FreeCharSlots");

			// Whether a client whose login block the server CANNOT decrypt may still log in. That block only decrypts
			// for a client patched with our DH public key (Faolan.ClientLauncher --patch-login), so an unpatched retail
			// client is authenticated on the account NAME alone - it never proves the password. Defaults to ON, the
			// historical behaviour: turning password validation on must not lock out the clients that predate it.
			// Turn it OFF (Login:AllowUnpatchedClients = false) for a deployment that hands out patched clients and
			// wants the password to actually be required.
			public bool LoginAllowUnpatchedClients => Bool(config, "Login:AllowUnpatchedClients");

			public double ChannelBacklogGraceMs => Double(config, "ChatBridge:BacklogGraceMs");
			public double ChannelPollMs => Double(config, "ChatBridge:PollMs");

			public string CdnMirrorIp => Required(config, "CdnMirror:Ip");

			// The client's RDB folder - part of the game INSTALL, not this repo, so it has no default. One key for
			// every consumer (Faolan.Web's viewer and the importer's rdb-* modules/reports) and every input method:
			// `RdbDir` in faolan.json, `RdbDir` in the environment, `--RdbDir <dir>` on the command line.
			public string? RdbDir => config["RdbDir"];

			// CDN content mirror (Faolan.Web.Content). All optional with the built-in defaults so the mirror runs with no
			// config at all; override in faolan.json, the environment, or on the command line - all under this one name.
			public string CdnMirrorStore => Required(config, "CdnMirror:Store");

			// The address the CLIENT reaches the mirror on, used to rewrite LocalConfig.xml's three patch-addr keys.
			// It is NOT the bind address: in a container CdnMirror:Ip is 0.0.0.0 by design, and on the Docker host the
			// client dials a published port. So only the operator knows it, and the mirror REFUSES TO START without
			// it - it used to derive one from the inbound Host header, which is not "what the client dialled" but
			// whatever the last hop put there. A bare `host` or `host:port` is fine - the scheme is filled in.
			public string? CdnMirrorPublicAddress => config["CdnMirror:PublicAddress"];

			// Where a CLIENT of the mirror dials it: the importer's RDB proxy and Faolan.Web's viewer. Same key, with
			// the ONE thing those two need that the mirror does not - a value when it is unset.
			//
			// `CdnMirrorPublicAddress` cannot simply be defaulted, because the two sides read "unset" differently and
			// both readings are right: to the MIRROR it means "I cannot know what a client would dial - refuse to
			// start", the only honest answer behind a published container port; to a CLIENT of the mirror it means
			// "the usual local one", because a client that guesses wrong just fails to connect, in front of the
			// operator. So the raw key stays nullable, which is what lets the mirror SEE that it is unset, and this
			// is the client's reading of it - a default, not a second setting.
			//
			// ⚠️ The value is an absolute URL, in the config too. Its consumers CONCATENATE onto it (`RdbReader`
			// appends the mirror route and then `/res/<md5>`), so a bare host would build a relative string that fails
			// at the HTTP call rather than at the config line that caused it.
			public string CdnMirrorAddress => config.CdnMirrorPublicAddress is { Length: > 0 } published
				? published.TrimEnd('/')
				: $"http://127.0.0.1:{config.CdnMirrorPort}";
			public string DatabaseConnection => Required(config, "Database:ConnectionString");

			// Log file location. Optional - defaults to faolan.log in the CWD; point it at a mounted volume (container)
			// or a per-run path via env/cmdline/json like any other config value.
			public string LogFile => Required(config, "Log:File");

			// Faolan.Mcp chat-bridge (the stdio "faolan-chat" MCP server). All optional with the built-in defaults so
			// the bridge runs with no config; McpUrl is the game-server MCP endpoint it connects to, the two Channel*
			// values tune its poll loop (milliseconds).
			public string McpUrl => Required(config, "Mcp:Url");
			// The client build the protocol was decoded against - every capture in the corpus is this one. A client
			// reporting anything else is not refused.
			public string ClientVersion => Required(config, "ClientVersion");
			public string ServerIpAddress => Required(config, "ServerIpAddress");
			public string WelcomeString => Required(config, "WelcomeString");

			public ushort CdnMirrorPort => UShort(config, "CdnMirror:Port");

			public ushort McpPort => UShort(config, "Mcp:Port");
			public ushort PlayerAgentPort => UShort(config, "PlayerAgentPort");
			public ushort UniverseAgentPort => UShort(config, "UniverseAgentPort");
		}

		// A value that must be there: either the catalog gives it a default or the deployment has to. Both failures
		// read the same to a caller, so they get the same message - one that names the key.
		private static string Required(IConfiguration config, string key)
		{
			return config[key] ?? throw new InvalidOperationException($"Missing required configuration '{key}' - set it in faolan.json or as an environment variable.");
		}

		// The typed readers. Each PARSES a value the defaults layer guarantees is present, and says which key was
		// malformed if it is not - where the `TryParse ? v : <literal>` these replaced silently substituted a value
		// the operator never wrote.
		private static bool Bool(IConfiguration config, string key)
		{
			return bool.TryParse(Required(config, key), out var value) ? value : throw Malformed(key, config[key], "true or false");
		}

		private static uint UInt(IConfiguration config, string key)
		{
			return uint.TryParse(Required(config, key), out var value) ? value : throw Malformed(key, config[key], "a whole number");
		}

		private static ushort UShort(IConfiguration config, string key)
		{
			return ushort.TryParse(Required(config, key), out var value) ? value : throw Malformed(key, config[key], "a port number (0-65535)");
		}

		private static double Double(IConfiguration config, string key)
		{
			return double.TryParse(Required(config, key), out var value) ? value : throw Malformed(key, config[key], "a number");
		}

		private static InvalidOperationException Malformed(string key, string? value, string expected)
		{
			return new InvalidOperationException($"Configuration '{key}' is '{value}', which is not {expected}.");
		}
	}
}
