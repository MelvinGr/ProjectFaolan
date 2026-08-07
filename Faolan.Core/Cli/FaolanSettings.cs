using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

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
	///         The layering that results: <b>defaults &lt; faolan.json &lt; faolan.user.json &lt; command line &lt;
	///         environment</b>. Because the bottom layer always supplies a value, a consumer reads
	///         <c>config["Log:File"]</c> and gets <c>faolan.log</c> - the <c>?? "faolan.log"</c> that used to sit at
	///         the read site is gone, and with it the chance of two read sites defaulting differently.
	///     </para>
	/// </remarks>
	public static class FaolanSettings
	{
		/// <summary>
		///     One setting. A null <paramref name="Default" /> means the setting is genuinely OPTIONAL - there is
		///     nothing this repo could put there, because the value names something outside it (a game install, a
		///     keypair, a deployment's public address). Everything else has one, so the server runs with no
		///     configuration file at all.
		/// </summary>
		public sealed record Setting(string Key, string Description, string? Default = null);

		// ⚠️ Two settings that look like they want a default here and must NOT have one.
		//
		// `Login:PrivateKey` - its default is `DhKeys`' built-in key, and writing the hex here would be a SECOND copy
		// of a keypair whose whole point is that there is one. `DhKeys.Configure(null)` keeps its own; that is why
		// this entry is a null default rather than the hex.
		//
		// `CdnMirror:PublicAddress` - the mirror and its clients read "unset" differently and both readings are
		// right: to the MIRROR it means "answer with the Host header the client just dialled" (#552); to a CLIENT of
		// the mirror it means "the usual local one". A default here would erase the mirror's reading. The client's
		// reading stays where it belongs, on `IConfigurationExtensions.CdnMirrorAddress`.
		public static readonly Setting[] All =
		[
			new("Database:Provider", "Sqlite or Postgres.", "Sqlite"),
			new("Database:ConnectionString", "The database to serve from; built from faolan.sql on first run if it has no content.", "Data Source=faolan.db"),
			new("ServerIpAddress", "The address the login tier advertises to clients - must be reachable FROM the client.", "127.0.0.1"),
			new("UniverseAgentPort", "Login-tier entry port (7010 is the client's fixed entry point).", "7010"),
			new("PlayerAgentPort", "Login-tier player-agent port.", "7011"),
			new("WelcomeString", "Login MOTD.", "Welcome to Project Faolan!"),
			new("TerrainDir", "Baked terrain directory; without it ground-snap and line-of-sight are disabled.", "../../ProjectFaolan-Data/Terrain"),
			new("RdbDir", "The client's RDB folder. Part of the game INSTALL, so it has no default."),
			new("ClientVersion", "The client build the protocol was decoded against. A client reporting anything else is not refused.", "v5.01.0@602248"),

			new("Login:PrivateKey", "The login DH private key (default: the built-in key). Patch clients with the matching public key - Faolan.ClientLauncher --patch-login."),
			new("Login:PublicKey", "Patch THIS public key into the exe instead of our derived one - for a client that should talk to somebody else's server."),
			new("Login:RetailPublicKey", "The key to search for in an unpatched exe (default: retail's)."),
			new("Login:AllowUnpatchedClients",
				"Let a client whose login block we cannot decrypt in on its account NAME alone. False requires a patched client, so the password is actually checked.", "true"),

			new("Account:Premium", "Advertise every account as subscribed.", "true"),
			new("Account:PremiumCharSlots", "Character slots advertised when premium.", "10"),
			new("Account:FreeCharSlots", "Character slots advertised when not premium.", "4"),

			new("Log:File", "Log file path.", "faolan.log"),
			new("Log:PacketBytes", "Include the raw byte dump in packet log lines.", "false"),

			new("Mcp:Enabled", "Host the in-process MCP endpoint.", "false"),
			new("Mcp:Port", "MCP endpoint port.", "7099"),
			new("Mcp:Url", "MCP endpoint URL the chat bridge connects to.", "http://127.0.0.1:7099/mcp"),

			new("ChatBridge:BacklogGraceMs", "How long the chat bridge treats a channel's history as backlog rather than new traffic, in milliseconds.", "120000"),
			new("ChatBridge:PollMs", "Chat-bridge poll interval, in milliseconds.", "1500"),

			new("CdnMirror:Store", "Directory the mirror stores fetched CDN blobs in.", "mirror-store"),
			new("CdnMirror:Ip", "Address the mirror binds.", "127.0.0.1"),
			new("CdnMirror:Port", "Port the mirror binds.", "7081"),
			new("CdnMirror:PublicAddress", "Address the CLIENT reaches the mirror on, written into LocalConfig.xml's patch addresses (default: the inbound request's Host header).")
		];

		private static readonly Dictionary<string, Setting> ByKey = All.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);

		/// <summary>The DEFAULTS configuration layer: every setting that has one. The bottom of <see cref="FaolanCommandLine.Build" />'s stack.</summary>
		public static IEnumerable<KeyValuePair<string, string?>> Defaults =>
			All.Where(s => s.Default != null).Select(s => new KeyValuePair<string, string?>(s.Key, s.Default));

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

		private static Option<string> Option(Setting setting)
		{
			// The default is SHOWN, not applied by the parser: applying it here would make every option "passed" and
			// so shadow faolan.json with the default, which is precisely the bug FaolanCommandLine.Overrides avoids by
			// dropping options the user did not pass.
			var description = setting.Default is { } value
				? $"{setting.Description} (default: {value})"
				: $"{setting.Description} (optional)";

			return new Option<string>($"--{setting.Key}") { Description = description };
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
				.AppendLine("// ⚠️ Machine-specific values do NOT belong here - this file is committed and must stay machine-agnostic. Put them")
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

		private static string? Section(Setting setting)
		{
			var colon = setting.Key.IndexOf(':');
			return colon < 0 ? null : setting.Key[..colon];
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

			var lines = new List<string> { $"\t\"{section}\": {{" };
			lines.AddRange(entries.SelectMany(e => e.Lines));
			lines.Add("\t}");

			return entries.Any(e => e.Live)
				? new Block(lines, true, true)
				: new Block([.. lines.Select(Comment)], false, true);
		}

		private static Block Entry(Setting setting, int depth)
		{
			var indent = new string('\t', depth);
			var name = depth == 1 ? setting.Key : Name(setting);
			var line = $"{indent}\"{name}\": {Literal(setting.Default ?? "")}";

			return new Block([$"{indent}// {setting.Description}", setting.Default == null ? Comment(line) : line], setting.Default != null);
		}

		// The comma goes after every LIVE entry but the last one, and the commented-out entries in between are skipped
		// - a comma on the last live line before a run of comments would close the object on a dangling separator.
		private static void Join(List<Block> blocks)
		{
			var last = blocks.FindLastIndex(b => b.Live);
			for (var i = 0; i < last; i++)
				if (blocks[i].Live)
					blocks[i].Lines[^1] += ",";
		}

		private static string Comment(string line)
		{
			var content = line.TrimStart('\t');
			return $"{line[..^content.Length]}//{content}";
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

		private sealed record Block(List<string> Lines, bool Live, bool Section = false);
	}
}
