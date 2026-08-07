using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Microsoft.Extensions.Configuration;

namespace Faolan.Core.Cli
{
	/// <summary>
	///     Every configuration setting this repo has, declared ONCE: its name, what <c>--help</c> says about it, and
	///     its DEFAULT.
	/// </summary>
	/// <remarks>
	///     <para>
	///         A setting used to be spelled out in two places that had to be kept in step by hand - the option
	///         declaration in a <c>*Command.cs</c> (name + description, for <c>--help</c>) and the accessor in
	///         <c>IConfigurationExtensions</c> (name + fallback, for the value). Neither knew about the other, so
	///         <c>--help</c> could describe a default the code did not use, and three settings
	///         (<c>ClientVersion</c>, the two <c>ChatBridge:*</c> ones) had a fallback and no declaration at all -
	///         invisible to every <c>--help</c> in the tree.
	///     </para>
	///     <para>
	///         ⚠️ <b>Why the catalog is shared rather than per-command.</b> A <c>*Command</c> declares only the subset
	///         it puts on ITS command line - <c>Faolan.Web</c> does not offer <c>Account:PremiumCharSlots</c>. If the
	///         default travelled with the declaration, an executable that reads a setting it does not declare would
	///         silently lose it. So the DEFAULTS layer is built from the whole catalog
	///         (<see cref="FaolanCommandLine.Build" />) and a command's option list is a VIEW of it.
	///     </para>
	///     <para>
	///         The layering that results:
	///         <b>
	///             defaults &lt; faolan.json &lt; faolan.user.json &lt; command line &lt; environment
	///         </b>
	///         . Because the bottom layer always supplies a value, a consumer reads
	///         <c>config["Log:File"]</c> and gets <c>faolan.log</c> - the <c>?? "faolan.log"</c> that used to sit at
	///         the read site is gone, and with it the chance of two read sites defaulting differently.
	///     </para>
	/// </remarks>
	public static class FaolanSettings
	{
		public static readonly ImmutableArray<Setting> All =
		[
			new Setting(Keys.DatabaseProvider, Enum.GetValues<FaolanDbProvider>().JoinAsString(", "), FaolanDbProvider.Sqlite),
			new Setting(Keys.DatabaseConnectionString, "The database to serve from; built from faolan.sql on first run if it has no content.", "Data Source=faolan.db"),
			new Setting(Keys.ServerAddress, "The address the login tier advertises to clients - must be reachable FROM the client. (Can be a dns record)", "127.0.0.1"),
			new Setting(Keys.ServerUniverseAgentPort, "UniverseAgent port. (Main entry point).", 7010),
			new Setting(Keys.ServerPlayerAgentPort, "PlayerAgent port.", 7011),
			new Setting(Keys.ServerWelcomeString, "Login MOTD.", "Welcome to Project Faolan!"),
			new Setting(Keys.ContentTerrainDir, "Baked terrain directory; without it ground-snap and line-of-sight are disabled.", "./terrain"),
			new Setting(Keys.ContentRdbDir, "The client's RDB folder. Usually not needed.", null),
			new Setting(Keys.ContentClientVersion, "The client build the protocol was decoded against. A client reporting anything else is not refused.", "v5.01.0@602248"),
			new Setting(Keys.LoginPrivateKey, "The server private key. Generate one with --generate-login-key.", null),
			new Setting(Keys.LoginPublicKey, "Patch clients with Faolan.ClientLauncher --patch-login. (Only used by Faolan.ClientLauncher)", null),
			new Setting(Keys.LoginAllowUnpatchedClients, "Let a unpatched client login with its account NAME alone. False requires a patched client & Login:PrivateKey filed.", true),
			new Setting(Keys.AccountPremium, "Advertise every account as subscribed.", true),
			new Setting(Keys.AccountPremiumCharSlots, "Character slots advertised when premium.", 10),
			new Setting(Keys.AccountFreeCharSlots, "Character slots advertised when not premium.", 4),
			new Setting(Keys.LogFile, "Log file path.", "faolan.log"),
			new Setting(Keys.LogPacketBytes, "Include the raw byte dump in packet log lines.", false),
			new Setting(Keys.McpEnabled, "Host the in-process MCP endpoint.", false),
			new Setting(Keys.McpPort, "MCP endpoint port.", 7099),
			new Setting(Keys.ChatBridgeEnabled, "Push in-game \"Claude\" channel chat into the Claude Code session. Off still leaves the bridge's reply tool working. (Requires Mcp:Enabled)", false),
			new Setting(Keys.ChatBridgeBacklogGraceMs, "How long the chat bridge treats a channel's history as backlog rather than new traffic, in milliseconds.", 120_000),
			new Setting(Keys.ChatBridgePollMs, "Chat-bridge poll interval, in milliseconds.", 1500),
			new Setting(Keys.CdnStore, "Directory the mirror stores fetched CDN blobs in.", "./cdn-store"),
			new Setting(Keys.CdnListenAddress, "Address the mirror BINDS - an interface, not the address a client dials.", "0.0.0.0"),
			new Setting(Keys.CdnListenPort, "Port the mirror BINDS - paired with Cdn:ListenAddress, not the port a client dials.", 7081),
			new Setting(Keys.CdnPublicAddress, "Address the CLIENT reaches the mirror on, Full URL, including scheme and port. (Only used by Faolan.ClientLauncher)", "http://127.0.0.1:7081")
		];

		/// <summary>
		///     The DEFAULTS configuration layer: every setting that has one. The bottom of
		///     <see cref="FaolanCommandLine.Build" />'s stack.
		/// </summary>
		public static IEnumerable<KeyValuePair<string, string?>> Defaults => All.Select(s => new KeyValuePair<string, string?>(s.Key, s.DefaultText));

		private static readonly Dictionary<string, Setting> ByKey = All.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);

		extension(IConfiguration config)
		{
			public BigInteger? LoginPrivateKey => config[Keys.LoginPrivateKey] is { } pk
				? HexExtensions.ToBigInteger(pk)
				: null;

			public BigInteger? LoginPublicKey => config[Keys.LoginPublicKey] is { } pk
				? HexExtensions.ToBigInteger(pk)
				: null;

			public FaolanDbProvider DatabaseProvider => Enum.Parse<FaolanDbProvider>(config[Keys.DatabaseProvider]!, true);

			public TimeSpan ChatBridgeBacklogGrace => TimeSpan.FromMilliseconds(config[Keys.ChatBridgeBacklogGraceMs]!.To<double>());
			public TimeSpan ChatBridgePoll => TimeSpan.FromMilliseconds(config[Keys.ChatBridgePollMs]!.To<double>());

			public bool AccountPremium => config[Keys.AccountPremium]!.To<bool>();

			public bool ChatBridgeEnabled => config.McpEnabled && config[Keys.ChatBridgeEnabled]!.To<bool>();

			public bool LogPacketBytes => config[Keys.LogPacketBytes]!.To<bool>();

			public bool LoginAllowUnpatchedClients => config[Keys.LoginAllowUnpatchedClients]!.To<bool>() || config.LoginPrivateKey == null;
			public bool McpEnabled => config[Keys.McpEnabled]!.To<bool>();

			public string CdnListenAddress => config[Keys.CdnListenAddress]!;

			public string CdnStore => config[Keys.CdnStore]!;

			public string ClientVersion => config[Keys.ContentClientVersion]!;

			public string DatabaseConnectionString => config[Keys.DatabaseConnectionString]!;

			public string LogFile => config[Keys.LogFile]!;

			public string McpUrl => $"http://127.0.0.1:{config.McpPort}/mcp";

			public string ServerAddress => config[Keys.ServerAddress]!;
			public string WelcomeString => config[Keys.ServerWelcomeString]!;

			public string? CdnPublicAddress => config[Keys.CdnPublicAddress];

			public string? RdbDir => config[Keys.ContentRdbDir];
			public string? TerrainDir => config[Keys.ContentTerrainDir];
			public uint AccountFreeCharSlots => config[Keys.AccountFreeCharSlots]!.To<uint>();

			public uint AccountPremiumCharSlots => config[Keys.AccountPremiumCharSlots]!.To<uint>();

			public ushort CdnListenPort => config[Keys.CdnListenPort]!.To<ushort>();

			public ushort McpPort => config[Keys.McpPort]!.To<ushort>();
			public ushort PlayerAgentPort => config[Keys.ServerPlayerAgentPort]!.To<ushort>();
			public ushort UniverseAgentPort => config[Keys.ServerUniverseAgentPort]!.To<ushort>();
		}

		/// <summary>
		///     The whole catalog as a faolan.json: every setting under its section, at its default, each above its
		///     <c>--help</c> description. A setting with no default is written as a COMMENTED-OUT line, so the file
		///     names every setting that exists while leaving the optional ones unset.
		/// </summary>
		/// <remarks>
		///     The configuration JSON reader skips <c>//</c> comments (and tolerates a trailing comma), which is what
		///     lets the generated file carry its own documentation. It is written by hand rather than through
		///     <c>JsonSerializer</c> for the same reason: a serializer would drop every comment.
		/// </remarks>
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
				.AppendLine("// in faolan.user.json (gitignored, same keys, it simply wins).")
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

				var run = new List<Setting>();
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

		/// <summary>The catalog entry for <paramref name="key" />.</summary>
		/// <exception cref="ArgumentException">No such setting - a typo here is a build-time mistake, not a runtime one.</exception>
		public static Setting Get(string key)
		{
			return ByKey.TryGetValue(key, out var setting)
				? setting
				: throw new ArgumentException($"'{key}' is not a Faolan setting. Add it to FaolanSettings.All - that is the only list.", nameof(key));
		}

		/// <summary>
		///     The configuration PATH of every setting, as a constant.
		/// </summary>
		/// <remarks>
		///     <para>
		///         A key is spelled in two places that have to agree - its <see cref="All" /> entry and the accessor
		///         that reads it - and as a string literal that agreement is a matter of care. A typo in an accessor
		///         resolves to <c>null</c> and then fails as an NRE or a parse error at whatever startup first needs
		///         the value, nowhere near the line that caused it; <see cref="Get" /> only guards the OTHER path, a
		///         <c>*Command</c>'s option list. As a const both paths are a compile error.
		///     </para>
		///     <para>
		///         Named for the setting rather than its section, so <c>Keys.LogFile</c> sits beside the
		///         <c>config.LogFile</c> that reads it, and ordered by name - which is also section order, since the
		///         names carry their section as a prefix, and it is where the member-layout cleanup leaves them.
		///     </para>
		/// </remarks>
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
			public const string ServerAddress = "Server:Address";
			public const string ServerPlayerAgentPort = "Server:PlayerAgentPort";
			public const string ServerUniverseAgentPort = "Server:UniverseAgentPort";
			public const string ServerWelcomeString = "Server:WelcomeString";
		}

		/// <summary>
		///     The command-line options for <paramref name="keys" />, in the order given: a <c>*Command</c>'s VIEW of
		///     the catalog. The description and the default come from the entry, so <c>--help</c> cannot describe a
		///     default the code does not use.
		/// </summary>
		public static Option<string>[] Options(params string[] keys)
		{
			return [.. keys.Select(Get).Select(Option)];
		}

		/// <summary>
		///     Writes <see cref="DefaultJson" /> to <paramref name="path" /> - a faolan.json holding every setting at
		///     its default.
		/// </summary>
		/// <remarks>
		///     ⚠️ It OVERWRITES. The generated file is a starting point, not a hook output: this repo's committed
		///     faolan.json is exactly this with a couple of values flipped for the dev run, and regenerating drops
		///     those (and anything else a deployment changed). Nothing calls it automatically for that reason.
		/// </remarks>
		public static void WriteDefaultJson(string path)
		{
			File.WriteAllText(path, DefaultJson());
		}

		private static string Comment(string line)
		{
			var content = line.TrimStart('\t');
			return $"{line[..^content.Length]}//{content}";
		}

		private static Block Entry(Setting setting, int depth)
		{
			var indent = new string('\t', depth);
			var name = depth == 1
				? setting.Key
				: Name(setting);
			var written = setting.Default != null;
			var line = $"{indent}\"{name}\": {Literal(setting.Default)}";

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

		private static string Name(Setting setting)
		{
			return setting.Key[(setting.Key.IndexOf(':') + 1)..];
		}

		private static Block Object(string section, List<Setting> settings)
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

		private static Option<string> Option(Setting setting)
		{
			var description = setting switch
			{
				{ DefaultText: { } value } => $"{setting.Description} (default: {value})",
				_                          => $"{setting.Description} (optional)"
			};

			return new Option<string>($"--{setting.Key}")
			{
				Description = description
			};
		}

		private static string? Section(Setting setting)
		{
			var colon = setting.Key.IndexOf(':');
			return colon < 0
				? null
				: setting.Key[..colon];
		}

		/// <summary>
		///     One setting. A null <paramref name="Default" /> means the setting is genuinely OPTIONAL - there is
		///     nothing this repo could put there, because the value names something outside it (a game install, this
		///     deployment's own keypair, a public address). Everything else has one, so the server runs with no
		///     configuration file at all.
		/// </summary>
		public sealed record Setting(string Key, string Description, object? Default)
		{
			/// <summary>
			///     The default as the configuration STACK carries it: every provider is string-keyed, so this is what
			///     lands in the defaults layer and what <c>--help</c> prints.
			/// </summary>
			/// <remarks>
			///     Invariant culture, and <c>true</c>/<c>false</c> lower-cased rather than .NET's <c>True</c>: the value
			///     is read back by parsers AND by people, and a float rendered under a comma-decimal locale would not
			///     round-trip through <c>float.Parse</c> at all. Rendering lives here, once, so the defaults layer and
			///     <see cref="Literal" /> cannot disagree about what a value looks like.
			/// </remarks>
			public string? DefaultText => Default switch
			{
				null => null, //"null",
				bool b => b
					? "true"
					: "false",
				string s       => s,
				Enum e         => e.ToString(),
				IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
				_              => Default.ToString()
			};
		}

		private sealed record Block(List<string> Lines, bool Live, bool Section = false);
	}
}
