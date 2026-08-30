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
using Faolan.Core.Crypt;
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
			Setting(Keys.AccountFreeCharSlots, "How many character slots an account gets when it is not premium.", 4u),
			Setting(Keys.AccountPremium, "Tell every client its account is subscribed, so nothing is gated behind a subscription.", true),
			Setting(Keys.AccountPremiumCharSlots, "How many character slots an account gets when it is premium.", 10u),
			Setting(Keys.CdnListenAddress, "The interface the CDN mirror BINDS. Not an address to dial - that is Cdn:PublicAddress.", "0.0.0.0"),
			Setting<ushort>(Keys.CdnListenPort, "The port the CDN mirror BINDS, on Cdn:ListenAddress. Not a port to dial - that is Cdn:PublicAddress.", 7081),
			Setting(Keys.CdnPublicAddress, "The full URL every mirror CLIENT dials, scheme and port included. http ONLY - the game client's libcurl has no SSL.", "http://127.0.0.1:7081"),
			Setting(Keys.CdnStore, "Where the CDN mirror keeps the blobs it has fetched.", "./cdn-store"),
			Setting(Keys.ContentClientVersion, "The client build the protocol was decoded against. Another build is WARNED about, never refused.", "v5.01.0@602248"),
			Setting(Keys.ContentKillStreaks, "Announce a player's kill STREAK - the running count of kills strung together inside nine seconds. Off, no chain is tracked and the client is told nothing.", true),
			Optional<string>(Keys.ContentRdbDir, "The client's RDB folder, part of the game INSTALL. Read by the importer, the RDB tools and Faolan.Web's model viewer - the server itself never needs it."),
			Setting(Keys.ContentTerrainDir, "Where the baked terrain lives. Without it the server still runs, with ground-snap and line-of-sight disabled.", "./terrain"),
			Setting(Keys.DatabaseConnectionString, "The database to serve from. One with no game content in it is filled from faolan.sql on first run.", "Data Source=faolan.db"),
			Setting(Keys.DatabaseProvider, $"The engine that database runs on. [{Enum.GetValues<FaolanDbProvider>().JoinAsString(", ")}]", FaolanDbProvider.Sqlite),
			Setting(Keys.LogFile, "Where the server writes its log, alongside the console.", "faolan.log"),
			Setting(Keys.LogPacketBytes, "Add the raw byte dump to every packet log line. Verbose - for protocol work.", false),
			Setting(Keys.McpEnabled, "Host the in-process MCP endpoint: database queries, live-session inspection and GM commands.", false),
			Setting<ushort>(Keys.McpPort, "The port that MCP endpoint binds. Loopback only, so there is no address to configure.", 7099),
			Setting(Keys.ServerListenAddress, "The interface the server BINDS. Not an address to dial - that is Server:PublicAddress.", "0.0.0.0"),
			Setting<ushort>(Keys.ServerPlayerAgentPort, "The port the PlayerAgent binds - the tier the UniverseAgent hands a client on to.", 7011),
			Optional<string>(Keys.ServerPrivateKey, $"The login DH private key, {ClientLoginEncryption.PrivateHexLength} hex characters - generate one with --generate-login-key. Unset, no password is ever checked; set, every client must be patched (Faolan.ClientLauncher --patch-login) or it is refused."),
			Setting(Keys.ServerPublicAddress, "The address the login tier ADVERTISES to clients - no port, and it must be reachable FROM the client. A DNS name is fine.", "127.0.0.1"),
			Optional<string>(Keys.ServerPublicKey, $"Patch THIS public key into clients, {ClientLoginEncryption.PublicHexLength} hex characters, instead of the one derived from Server:PrivateKey - for a client that talks to somebody ELSE's server. Only Faolan.ClientLauncher reads it."),
			Setting<ushort>(Keys.ServerUniverseAgentPort, "The port the UniverseAgent binds - where a client enters, and the only port it has to be told about.", 7010),
			Setting(Keys.ServerWelcomeString, "The message a client is greeted with at login. $BUILDSTRING expands to this server's build.", "Welcome to Project Faolan!")
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
		// One kind of member is legitimately not a key: a DERIVED value (`McpUrl`, `ServerUniverseAddress`), which sits
		// directly under the setting it is derived from.
		extension(IConfiguration config)
		{
			public uint AccountFreeCharSlots => Value<uint>(config, Keys.AccountFreeCharSlots);
			public bool AccountPremium => Value<bool>(config, Keys.AccountPremium);
			public uint AccountPremiumCharSlots => Value<uint>(config, Keys.AccountPremiumCharSlots);

			public IPAddress CdnListenAddress => IPAddress.Parse(config[Keys.CdnListenAddress]!);
			public ushort CdnListenPort => Value<ushort>(config, Keys.CdnListenPort);
			public string? CdnPublicAddress => config[Keys.CdnPublicAddress];
			public string CdnStore => config[Keys.CdnStore]!;

			public string ContentClientVersion => config[Keys.ContentClientVersion]!;
			public bool ContentKillStreaks => Value<bool>(config, Keys.ContentKillStreaks);
			public string? ContentRdbDir => config[Keys.ContentRdbDir];
			public string? ContentTerrainDir => config[Keys.ContentTerrainDir];

			public string DatabaseConnectionString => config[Keys.DatabaseConnectionString]!;
			public FaolanDbProvider DatabaseProvider => Choice<FaolanDbProvider>(config, Keys.DatabaseProvider);

			public string LogFile => config[Keys.LogFile]!;
			public bool LogPacketBytes => Value<bool>(config, Keys.LogPacketBytes);

			public bool McpEnabled => Value<bool>(config, Keys.McpEnabled);
			public ushort McpPort => Value<ushort>(config, Keys.McpPort);
			public string McpUrl => $"http://127.0.0.1:{config.McpPort}/mcp";

			public IPAddress ServerListenAddress => IPAddress.Parse(config[Keys.ServerListenAddress]!);
			public ushort ServerPlayerAgentPort => Value<ushort>(config, Keys.ServerPlayerAgentPort);

			public BigInteger? ServerPrivateKey => config[Keys.ServerPrivateKey] is {} pk
				? pk.Length == ClientLoginEncryption.PrivateHexLength
					? HexExtensions.ToBigInteger(pk)
					: throw new InvalidOperationException($"{Keys.ServerPrivateKey} must be {ClientLoginEncryption.PrivateHexLength} characters long")
				: null;

			public string ServerPublicAddress => config[Keys.ServerPublicAddress] ?? throw new InvalidOperationException($"{Keys.ServerPublicAddress} is required");

			// 256, not the private key's 64: a DH public key is a value mod the 1024-bit prime, and 256 hex chars is
			// the fixed ASCII field the exe stores one in - the width the launcher patches and the width somebody
			// else's server would hand you.
			public BigInteger? ServerPublicKey => config[Keys.ServerPublicKey] is {} pk
				? pk.Length == ClientLoginEncryption.PublicHexLength
					? HexExtensions.ToBigInteger(pk)
					: throw new InvalidOperationException($"{Keys.ServerPublicKey} must be {ClientLoginEncryption.PublicHexLength} characters long")
				: null;

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
			public const string ContentClientVersion = "Content:ClientVersion";
			public const string ContentKillStreaks = "Content:KillStreaks";
			public const string ContentRdbDir = "Content:RdbDir";
			public const string ContentTerrainDir = "Content:TerrainDir";
			public const string DatabaseConnectionString = "Database:ConnectionString";
			public const string DatabaseProvider = "Database:Provider";
			public const string LogFile = "Log:File";
			public const string LogPacketBytes = "Log:PacketBytes";
			public const string McpEnabled = "Mcp:Enabled";
			public const string McpPort = "Mcp:Port";
			public const string ServerListenAddress = "Server:ListenAddress";
			public const string ServerPlayerAgentPort = "Server:PlayerAgentPort";
			public const string ServerPrivateKey = "Server:PrivateKey";
			public const string ServerPublicAddress = "Server:PublicAddress";
			public const string ServerPublicKey = "Server:PublicKey";
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

		// An OPTIONAL setting is written as JSON `null`, not as a commented-out line and not as "": the file then NAMES
		// every key the catalog holds, which is the point of generating it, and null is what an unset setting resolves
		// to anyway (the configuration provider reads a JSON null back as no value, so the accessor sees exactly the
		// state it would if the line were absent). An empty string would not be that state - it is a VALUE, and a key
		// that parses its text has to reject it.
		private static Block Entry(Option setting, int depth)
		{
			var indent = new string('\t', depth);
			var name = depth == 1
				? setting.Key()
				: Name(setting);

			return new Block([
				$"{indent}// {setting.Description}",
				$"{indent}\"{name}\": {Literal(setting)}"
			]);
		}

		private static void Join(List<Block> blocks)
		{
			for (var i = 0; i < blocks.Count - 1; i++)
				blocks[i].Lines[^1] += ",";
		}

		private static string Literal(Option setting)
		{
			return setting.HasDefaultValue
				? Literal(setting.GetDefaultValue())
				: "null";
		}

		private static string Literal(object? value)
		{
			return value switch
			{
				null => "null",
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

			return new Block(lines, true);
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

		private sealed record Block(List<string> Lines, bool Section = false);
	}
}
