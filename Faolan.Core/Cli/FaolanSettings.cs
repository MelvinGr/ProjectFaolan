using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.CommandLine;
using System.IO;
using System.Linq;
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
	///             defaults &lt; faolan.json &lt; faolan.user.json &lt; command line &lt;
	///             environment
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
			new Setting("Database:Provider", "Sqlite or Postgres.", "Sqlite"),
			new Setting("Database:ConnectionString", "The database to serve from; built from faolan.sql on first run if it has no content.", "Data Source=faolan.db"),
			new Setting("ServerIpAddress", "The address the login tier advertises to clients - must be reachable FROM the client.", "127.0.0.1"),
			new Setting("UniverseAgentPort", "UniverseAgent port. (Main entry point).", "7010"), new Setting("PlayerAgentPort", "PlayerAgent port.", "7011"),
			new Setting("WelcomeString", "Login MOTD.", "Welcome to Project Faolan!"),
			new Setting("TerrainDir", "Baked terrain directory; without it ground-snap and line-of-sight are disabled.", "./terrain"),
			new Setting("RdbDir", "The client's RDB folder. Usually not needed."),
			new Setting("ClientVersion", "The client build the protocol was decoded against. A client reporting anything else is not refused.", "v5.01.0@602248"),
			new Setting("Login:PrivateKey", "The server private key (Generate with --generate-login-key)"),
			new Setting("Login:PublicKey", "Patch clients with Faolan.ClientLauncher --patch-login."),
			new Setting("Login:AllowUnpatchedClients",
				"Let a client whose login block we cannot decrypt in on its account NAME alone. False requires a patched client, so the password is actually checked.", "true"),
			new Setting("Account:Premium", "Advertise every account as subscribed.", "true"), new Setting("Account:PremiumCharSlots", "Character slots advertised when premium.", "10"),
			new Setting("Account:FreeCharSlots", "Character slots advertised when not premium.", "4"), new Setting("Log:File", "Log file path.", "faolan.log"),
			new Setting("Log:PacketBytes", "Include the raw byte dump in packet log lines.", "false"), new Setting("Mcp:Enabled", "Host the in-process MCP endpoint.", "false"),
			new Setting("Mcp:Port", "MCP endpoint port.", "7099"),
			new Setting("ChatBridge:Url", "MCP endpoint the chat bridge DIALS. Defaults to the local server's own Mcp:Port, so set it only for a bridge on another host."),
			new Setting("ChatBridge:BacklogGraceMs", "How long the chat bridge treats a channel's history as backlog rather than new traffic, in milliseconds.", "120000"),
			new Setting("ChatBridge:PollMs", "Chat-bridge poll interval, in milliseconds.", "1500"), new Setting("Cdn:Store", "Directory the mirror stores fetched CDN blobs in.", "mirror-store"),
			new Setting("Cdn:ListenAddress", "Address the mirror BINDS - an interface, not the address a client dials.", "127.0.0.1"),
			new Setting("Cdn:ListenPort", "Port the mirror BINDS - paired with Cdn:ListenAddress, not the port a client dials.", "7081"),
			new Setting("Cdn:PublicAddress", "Address the CLIENT reaches the mirror on, written into LocalConfig.xml's patch addresses. Full URL, including scheme and port.")
		];

		/// <summary>
		///     The DEFAULTS configuration layer: every setting that has one. The bottom of
		///     <see cref="FaolanCommandLine.Build" />'s stack.
		/// </summary>
		public static IEnumerable<KeyValuePair<string, string?>> Defaults => All.Where(s => s.Default != null).Select(s => new KeyValuePair<string, string?>(s.Key, s.Default));

		private static readonly Dictionary<string, Setting> ByKey = All.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);

		extension(IConfiguration config)
		{
			public FaolanDbProvider DatabaseProvider => Enum.Parse<FaolanDbProvider>(config["Database:Provider"], true);

			public bool AccountPremium => config["Account:Premium"].To<bool>();

			public bool LogPacketBytes => config["Log:PacketBytes"].To<bool>();

			public bool LoginAllowUnpatchedClients => config["Login:AllowUnpatchedClients"].To<bool>();
			public bool McpEnabled => config["Mcp:Enabled"].To<bool>();

			public float ChatBridgeBacklogGraceMs => config["ChatBridge:BacklogGraceMs"].To<float>();
			public float ChatBridgePollMs => config["ChatBridge:PollMs"].To<float>();

			public string CdnListenAddress => config["Cdn:ListenAddress"];

			public string CdnStore => config["Cdn:Store"];

			public string ChatBridgeUrl => config["ChatBridge:Url"];

			public string ClientVersion => config["ClientVersion"];

			public string DatabaseConnectionString => config["Database:ConnectionString"];

			public string LogFile => config["Log:File"];
			public string ServerIpAddress => config["ServerIpAddress"];
			public string WelcomeString => config["WelcomeString"];

			public string? CdnPublicAddress => config["Cdn:PublicAddress"];

			public string? LoginPrivateKey => config["Login:PrivateKey"];
			public string? LoginPublicKey => config["Login:PublicKey"];

			public string? RdbDir => config["RdbDir"];
			public uint AccountFreeCharSlots => config["Account:FreeCharSlots"].To<uint>();

			public uint AccountPremiumCharSlots => config["Account:PremiumCharSlots"].To<uint>();

			public ushort CdnListenPort => config["Cdn:ListenPort"].To<ushort>();

			public ushort McpPort => config["Mcp:Port"].To<ushort>();
			public ushort PlayerAgentPort => config["PlayerAgentPort"].To<ushort>();
			public ushort UniverseAgentPort => config["UniverseAgentPort"].To<ushort>();
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
				.AppendLine("// The commented-out keys are the OPTIONAL ones - they name something outside this repo (a game install, a keypair,")
				.AppendLine("// a deployment's public address), so there is nothing it could default them to.")
				.AppendLine("//")
				.AppendLine("// Machine-specific values do NOT belong here - this file is committed and must stay machine-agnostic. Put them")
				.AppendLine("// in faolan.user.json (gitignored, same keys, it simply wins).")
				.AppendLine("{");

			// The catalog's ORDER is the file's order, and a section is a RUN of consecutive keys sharing a prefix -
			// which is why the catalog groups them. Nothing sorts here: the order is editorial (what a reader needs
			// first) and a sort would scatter it.
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
				// A blank line either side of a section object, so a run of top-level keys and the object next to it do
				// not read as one run.
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
			var written = setting is { Default: not null, Hidden: false };
			var line = $"{indent}\"{name}\": {Literal(written ? setting.Default! : "")}";

			return new Block([
				$"{indent}// {setting.Description}", written
					? line
					: Comment(line)
			], written);
		}

		// The comma goes after every LIVE entry but the last one, and the commented-out entries in between are skipped
		// - a comma on the last live line before a run of comments would close the object on a dangling separator.
		private static void Join(List<Block> blocks)
		{
			var last = blocks.FindLastIndex(b => b.Live);
			for (var i = 0; i < last; i++)
			{
				if (blocks[i].Live)
					blocks[i].Lines[^1] += ",";
			}
		}

		// A default is stored as TEXT (it is a configuration value), so the JSON it renders as follows its shape:
		// true/false and whole numbers unquoted, everything else a string. Getting this wrong would be invisible here
		// and load fine either way - the configuration binder reads every value back as text - but the file is read by
		// people too, and a quoted port reads as a typo.
		private static string Literal(string value)
		{
			return value is "true" or "false" || (value.Length > 0 && value.All(char.IsAsciiDigit))
				? value
				: JsonSerializer.Serialize(value);
		}

		private static string Name(Setting setting)
		{
			return setting.Key[(setting.Key.IndexOf(':') + 1)..];
		}

		// One "Section": { … } object. A section whose settings are ALL optional is commented out whole - an empty
		// object would read as a section with nothing in it rather than one nobody has set.
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
			// The default is SHOWN, not applied by the parser: applying it here would make every option "passed" and
			// so shadow faolan.json with the default, which is precisely the bug FaolanCommandLine.Overrides avoids by
			// dropping options the user did not pass.
			var description = setting switch
			{
				{ Hidden: true }       => setting.Description, // ...which says what its default is in words
				{ Default: { } value } => $"{setting.Description} (default: {value})",
				_                      => $"{setting.Description} (optional)"
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
		///     nothing this repo could put there, because the value names something outside it (a game install, a
		///     keypair, a deployment's public address). Everything else has one, so the server runs with no
		///     configuration file at all.
		/// </summary>
		/// <param name="Hidden">
		///     The default APPLIES but is not written into the generated config file, nor spelled out in
		///     <c>--help</c>. For the two key literals: a config file carrying key material invites someone to edit
		///     half of a pair, and a 256-char hex line in a `--help` listing is noise. The description says what the
		///     default is in words instead.
		/// </param>
		public sealed record Setting(string Key, string Description, string? Default = null, bool Hidden = false);

		private sealed record Block(List<string> Lines, bool Live, bool Section = false);
	}
}
