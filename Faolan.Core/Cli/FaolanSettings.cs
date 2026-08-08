using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using JetBrains.Annotations;
using Microsoft.Extensions.Configuration;

namespace Faolan.Core.Cli
{
	// [NoReorder] because this file is THREE views of one list - the catalog, the accessors, the key constants - and
	// they are all in the same order, ordinal by key, so a reader can scan them side by side and a missing entry shows
	// up as a gap. ReSharper's layout would otherwise regroup the accessors by RETURN TYPE (it has, twice), which
	// interleaves the sections into an order nothing else in the tree shares. The solution's own layout pattern
	// exempts a type carrying this attribute (see ProjectFaolan.sln.DotSettings, "Non-reorderable types"), and
	// FaolanSettingsTests.TheCatalog_IsInKeyOrder pins the catalog half, which no editor setting can.
	[NoReorder]
	public static class FaolanSettings
	{
		public static readonly ImmutableArray<Option> All =
		[
			Setting(Keys.AccountFreeCharSlots, "Character slots advertised when not premium.", 4u),
			Setting(Keys.AccountPremium, "Advertise every account as subscribed.", true),
			Setting(Keys.AccountPremiumCharSlots, "Character slots advertised when premium.", 10u),
			Setting(Keys.CdnListenAddress, "Address the mirror BINDS - an interface, not the address a client dials.", "0.0.0.0"),
			Setting<ushort>(Keys.CdnListenPort, "Port the mirror BINDS - paired with Cdn:ListenAddress, not the port a client dials.", 7081),
			Setting(Keys.CdnPublicAddress, "Address the CLIENT reaches the mirror on, Full URL, including scheme and port. (http ONLY)", "http://127.0.0.1:7081"),
			Setting(Keys.CdnStore, "Directory the mirror stores fetched CDN blobs in.", "./cdn-store"),
			Setting(Keys.ChatBridgeBacklogGraceMs, "How long the chat bridge treats a channel's history as backlog rather than new traffic, in milliseconds.", 120_000),
			Setting(Keys.ChatBridgeEnabled, "Push in-game \"Claude\" channel chat into the Claude Code session. Off still leaves the bridge's reply tool working. (Requires Mcp:Enabled)", false),
			Setting(Keys.ChatBridgePollMs, "Chat-bridge poll interval, in milliseconds.", 1500),
			Setting(Keys.ContentClientVersion, "The client build the protocol was decoded against. A client reporting anything else is not refused.", "v5.01.0@602248"),
			Optional<string>(Keys.ContentRdbDir, "The client's RDB folder. Usually not needed."),
			Setting(Keys.ContentTerrainDir, "Baked terrain directory; without it ground-snap and line-of-sight are disabled.", "./terrain"),
			Setting(Keys.DatabaseConnectionString, "The database to serve from; built from faolan.sql on first run if it has no content.", "Data Source=faolan.db"),
			Setting(Keys.DatabaseProvider, "The database engine to serve from.", FaolanDbProvider.Sqlite),
			Setting(Keys.LogFile, "Log file path.", "faolan.log"),
			Setting(Keys.LogPacketBytes, "Include the raw byte dump in packet log lines.", false),
			Setting(Keys.LoginAllowUnpatchedClients, "Let a unpatched client login with its account NAME alone. False requires a patched client & Login:PrivateKey filed.", true),
			Optional<string>(Keys.LoginPrivateKey, "The server private key. Generate one with --generate-login-key."),
			Optional<string>(Keys.LoginPublicKey, "Patch clients with Faolan.ClientLauncher --patch-login. (Only used by Faolan.ClientLauncher)"),
			Setting(Keys.McpEnabled, "Host the in-process MCP endpoint.", false),
			Setting<ushort>(Keys.McpPort, "MCP endpoint port.", 7099),
			Setting(Keys.ServerListenAddress, "Address the server BINDS - an interface, not the address a client dials.", "0.0.0.0"),
			Setting<ushort>(Keys.ServerPlayerAgentPort, "PlayerAgent port.", 7011),
			Setting(Keys.ServerPublicAddress, "The address the login tier advertises to clients, Just address (no port), must be reachable FROM the client. (Can be a dns record)", "127.0.0.1"),
			Setting<ushort>(Keys.ServerUniverseAgentPort, "UniverseAgent port. (Main entry point).", 7010),
			Setting(Keys.ServerWelcomeString, "Login MOTD.", "Welcome to Project Faolan!\n$BUILDSTRING")
		];

		public static IEnumerable<KeyValuePair<string, string?>> Defaults => All.Select(setting => new KeyValuePair<string, string?>(setting.Key(), DefaultText(setting)));

		// Declared AFTER All on purpose: a static field initializer runs in textual order, so this one needs the
		// catalog to exist already.
		private static readonly Dictionary<string, Option> ByKey = All.ToDictionary(Key, StringComparer.OrdinalIgnoreCase);

		// One accessor per catalog entry, IN CATALOG ORDER (see [NoReorder] above), NAMED for its key with the colon
		// dropped - `Server:UniverseAgentPort` reads back as `config.ServerUniverseAgentPort`, exactly as the Keys
		// const beside it is named. So a key and its accessor are one grep, and an accessor cannot drift into naming a
		// section it does not belong to (`UniverseAgentPort`, `WelcomeString`, `RdbDir` all read as global).
		//
		// Two kinds of member are legitimately not a key: a DERIVED value (`McpUrl`, `ServerUniverseAddress`), which
		// sits directly under the setting it is derived from, and a UNIT CONVERSION (`ChatBridgeBacklogGrace`,
		// `ChatBridgePoll`) - the key carries the Ms suffix because it holds a number, the accessor drops it because it
		// hands back a TimeSpan, and keeping the suffix there would be the lie.
		extension(IConfiguration config)
		{
			public uint AccountFreeCharSlots => Value<uint>(config, Keys.AccountFreeCharSlots);
			public bool AccountPremium => Value<bool>(config, Keys.AccountPremium);
			public uint AccountPremiumCharSlots => Value<uint>(config, Keys.AccountPremiumCharSlots);

			public IPAddress CdnListenAddress => IPAddress.Parse(config[Keys.CdnListenAddress]!);
			public ushort CdnListenPort => Value<ushort>(config, Keys.CdnListenPort);
			public string? CdnPublicAddress => config[Keys.CdnPublicAddress];
			public string CdnStore => config[Keys.CdnStore]!;

			public TimeSpan ChatBridgeBacklogGrace => TimeSpan.FromMilliseconds(Value<int>(config, Keys.ChatBridgeBacklogGraceMs));
			public bool ChatBridgeEnabled => config.McpEnabled && Value<bool>(config, Keys.ChatBridgeEnabled);
			public TimeSpan ChatBridgePoll => TimeSpan.FromMilliseconds(Value<int>(config, Keys.ChatBridgePollMs));

			public string ContentClientVersion => config[Keys.ContentClientVersion]!;
			public string? ContentRdbDir => config[Keys.ContentRdbDir];
			public string? ContentTerrainDir => config[Keys.ContentTerrainDir];

			public string DatabaseConnectionString => config[Keys.DatabaseConnectionString]!;
			public FaolanDbProvider DatabaseProvider => Choice<FaolanDbProvider>(config, Keys.DatabaseProvider);

			public string LogFile => config[Keys.LogFile]!;
			public bool LogPacketBytes => Value<bool>(config, Keys.LogPacketBytes);

			public bool LoginAllowUnpatchedClients => Value<bool>(config, Keys.LoginAllowUnpatchedClients) || config.LoginPrivateKey == null;

			public BigInteger? LoginPrivateKey => config[Keys.LoginPrivateKey] is { Length: > 0 } pk
				? HexExtensions.ToBigInteger(pk)
				: null;

			public BigInteger? LoginPublicKey => config[Keys.LoginPublicKey] is { Length: > 0 } pk
				? HexExtensions.ToBigInteger(pk)
				: null;

			public bool McpEnabled => Value<bool>(config, Keys.McpEnabled);
			public ushort McpPort => Value<ushort>(config, Keys.McpPort);
			public string McpUrl => $"http://127.0.0.1:{config.McpPort}/mcp";

			public IPAddress ServerListenAddress => IPAddress.Parse(config[Keys.ServerListenAddress]!);
			public ushort ServerPlayerAgentPort => Value<ushort>(config, Keys.ServerPlayerAgentPort);
			public string ServerPublicAddress => config[Keys.ServerPublicAddress] ?? throw new InvalidOperationException($"{Keys.ServerPublicAddress} is required");

			// Where a CLIENT dials to log in - the ADVERTISED address (never Server:ListenAddress, which is an
			// interface: 0.0.0.0 is by design unreachable as a destination) and the UniverseAgent port, which is the
			// pair the client's LocalConfig.xml <UniverseAddr> holds. Derived rather than a setting of its own, like
			// McpUrl: a second copy of either half is a copy that can disagree with the port the server binds.
			public string ServerUniverseAddress => $"{config.ServerPublicAddress}:{config.ServerUniverseAgentPort}";
			public ushort ServerUniverseAgentPort => Value<ushort>(config, Keys.ServerUniverseAgentPort);
			public string ServerWelcomeString => config[Keys.ServerWelcomeString] ?? "";
		}

		public static string DefaultJson()
		{
			var text = new StringBuilder()
				.AppendLine("// Project Faolan configuration - every setting, at its default.")
				.AppendLine("//")
				.AppendLine("// GENERATED by FaolanSettings.WriteDefaultJson(); the catalog it comes from (Faolan.Core/Cli/FaolanSettings.cs) is")
				.AppendLine("// also the bottom layer of the configuration stack, so a key deleted from this file still has its default. The")
				.AppendLine("// layering is: defaults < faolan.json < faolan.user.json < command line < environment.")
				.AppendLine("//")
				.AppendLine("// Machine-specific values do NOT belong here - this file is committed and must stay machine-agnostic. Put them")
				.AppendLine("// in faolan.user.json for example.")
				.AppendLine("{");

			var blocks = new List<Block>();
			for (var i = 0; i < All.Length; i++)
			{
				var section = Section(All[i]);
				if (section == null)
				{
					blocks.Add(Entry(All[i], 1));
					continue;
				}

				var run = new List<Option>();
				while (i < All.Length && Section(All[i]) == section)
					run.Add(All[i++]);
				i--;

				blocks.Add(Object(section, run));
			}

			Join(blocks);
			for (var i = 0; i < blocks.Count; i++)
			{
				if (i > 0 && (blocks[i].Section || blocks[i - 1].Section))
					text.AppendLine();

				foreach (var line in blocks[i].Lines)
					text.AppendLine(line);
			}

			return text.AppendLine("}").ToString();
		}

		public static string? DefaultText(Option setting)
		{
			return setting.HasDefaultValue
				? Text(setting.GetDefaultValue())
				: null;
		}

		public static Option Get(string key)
		{
			return ByKey.TryGetValue(key, out var setting)
				? setting
				: throw new ArgumentException($"'{key}' is not a Faolan setting. Add it to FaolanSettings.All - that is the only list.", nameof(key));
		}

		public static string Key(this Option setting)
		{
			return setting.Name.TrimStart('-');
		}

		public static class Keys
		{
			public const string AccountFreeCharSlots = "Account:FreeCharSlots";
			public const string AccountPremium = "Account:Premium";
			public const string AccountPremiumCharSlots = "Account:PremiumCharSlots";
			public const string CdnListenAddress = "Cdn:ListenAddress";
			public const string CdnListenPort = "Cdn:ListenPort";
			public const string CdnPublicAddress = "Cdn:PublicAddress";
			public const string CdnStore = "Cdn:Store";
			public const string ChatBridgeBacklogGraceMs = "ChatBridge:BacklogGraceMs";
			public const string ChatBridgeEnabled = "ChatBridge:Enabled";
			public const string ChatBridgePollMs = "ChatBridge:PollMs";
			public const string ContentClientVersion = "Content:ClientVersion";
			public const string ContentRdbDir = "Content:RdbDir";
			public const string ContentTerrainDir = "Content:TerrainDir";
			public const string DatabaseConnectionString = "Database:ConnectionString";
			public const string DatabaseProvider = "Database:Provider";
			public const string LogFile = "Log:File";
			public const string LogPacketBytes = "Log:PacketBytes";
			public const string LoginAllowUnpatchedClients = "Login:AllowUnpatchedClients";
			public const string LoginPrivateKey = "Login:PrivateKey";
			public const string LoginPublicKey = "Login:PublicKey";
			public const string McpEnabled = "Mcp:Enabled";
			public const string McpPort = "Mcp:Port";
			public const string ServerListenAddress = "Server:ListenAddress";
			public const string ServerPlayerAgentPort = "Server:PlayerAgentPort";
			public const string ServerPublicAddress = "Server:PublicAddress";
			public const string ServerUniverseAgentPort = "Server:UniverseAgentPort";
			public const string ServerWelcomeString = "Server:WelcomeString";
		}

		public static Option[] Options(params string[] keys)
		{
			return [.. keys.Select(Get)];
		}

		public static void WriteDefaultJson(string path)
		{
			File.WriteAllText(path, DefaultJson());
		}

		internal static string? Text(object? value)
		{
			return value switch
			{
				null => null,
				bool b => b
					? "true"
					: "false",
				string s       => s,
				Enum e         => e.ToString(),
				IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
				_              => value.ToString()
			};
		}

		private static T Choice<T>(IConfiguration config, string key) where T : struct, Enum
		{
			if (config[key] is not { } text)
				throw new InvalidOperationException($"'{key}' has no value. Every setting has one - it comes from FaolanSettings.All, the bottom layer of the configuration stack.");

			return Enum.TryParse<T>(text, true, out var value) && Enum.IsDefined(value)
				? value
				: throw new InvalidOperationException(
					$"'{key}' is set to '{text}', which is not one of {Enum.GetValues<T>().JoinAsString(", ")}. Fix it in faolan.json, faolan.user.json, the command line or the environment.");
		}

		private static string Comment(string line)
		{
			var content = line.TrimStart('\t');
			return $"{line[..^content.Length]}//{content}";
		}

		private static Block Entry(Option setting, int depth)
		{
			var indent = new string('\t', depth);
			var name = depth == 1
				? setting.Key()
				: Name(setting);
			var written = setting.HasDefaultValue;
			var line = $"{indent}\"{name}\": {Literal(setting)}";

			return new Block([
				$"{indent}// {setting.Description}", written
					? line
					: Comment(line)
			], written);
		}

		private static void Join(List<Block> blocks)
		{
			var last = blocks.FindLastIndex(b => b.Live);
			for (var i = 0; i < last; i++)
			{
				if (blocks[i].Live)
					blocks[i].Lines[^1] += ",";
			}
		}

		private static string Literal(Option setting)
		{
			return setting.HasDefaultValue
				? Literal(setting.GetDefaultValue())
				: "\"\"";
		}

		private static string Literal(object? value)
		{
			return value switch
			{
				null => "\"\"",
				bool b => b
					? "true"
					: "false",
				string s       => JsonSerializer.Serialize(s),
				Enum e         => JsonSerializer.Serialize(e.ToString()),
				IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
				_              => JsonSerializer.Serialize(value.ToString())
			};
		}

		private static string Name(Option setting)
		{
			var key = setting.Key();
			return key[(key.IndexOf(':') + 1)..];
		}

		private static Block Object(string section, List<Option> settings)
		{
			var entries = settings.Select(s => Entry(s, 2)).ToList();
			Join(entries);

			var lines = new List<string>
			{
				$"\t\"{section}\": {{"
			};
			lines.AddRange(entries.SelectMany(e => e.Lines));
			lines.Add("\t}");

			return entries.Any(e => e.Live)
				? new Block(lines, true, true)
				: new Block([.. lines.Select(Comment)], false, true);
		}

		private static Option<T> Optional<T>(string key, string description)
		{
			return new Option<T>($"--{key}")
			{
				Description = description
			};
		}

		private static string? Section(Option setting)
		{
			var key = setting.Key();
			var colon = key.IndexOf(':');

			return colon < 0
				? null
				: key[..colon];
		}

		private static Option<T> Setting<T>(string key, string description, T @default)
		{
			return new Option<T>($"--{key}")
			{
				Description = description,
				DefaultValueFactory = _ => @default
			};
		}

		private static T Value<T>(IConfiguration config, string key) where T : IParsable<T>
		{
			if (config[key] is not { } text)
				throw new InvalidOperationException($"'{key}' has no value. Every setting has one - it comes from FaolanSettings.All, the bottom layer of the configuration stack.");

			return T.TryParse(text, CultureInfo.InvariantCulture, out var value)
				? value
				: throw new InvalidOperationException($"'{key}' is set to '{text}', which is not a {typeof(T).Name}. Fix it in faolan.json, faolan.user.json, the command line or the environment.");
		}

		private sealed record Block(List<string> Lines, bool Live, bool Section = false);
	}
}
