using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Faolan.ClientLauncher
{
	/// <summary>
	///     Turns on the retail client's own debug channels (`GameCode/Stats`, `GameCode/ArmorMitigation`,
	///     `GameCode/ItemData`, `ClientInterface`, `N3.IIR`, …) by writing its logger config file, so the client reports
	///     what it computed instead of it having to be inferred from screenshots.
	/// </summary>
	/// <remarks>
	///     The client reads <c>dbDebug.conf</c> from its install dir at startup (the path is built at
	///     <c>0x004130C7</c>, the parser is <c>FUN_004AB950</c>). The parser skips blank and <c>#</c> lines, then
	///     full-line-matches each line against a fixed table of settings keys at <c>0x01F847C4</c> — and every line that
	///     matches none of them is registered as a debug channel:
	///     <code>
	///     name  = tok(line)                                  ; delimiters " =\n\r"
	///     value = tok()
	///     FUN_004B2A40(name, value != null ? atoi(value) : defaultLevel)
	///     </code>
	///     <c>FUN_004B2A40</c> is the same "add channel" routine that fills the live name table at <c>0x02984AE0</c> and
	///     its parallel i32 thresholds at <c>0x029862E0</c>. A line is emitted when <c>threshold &lt;= level</c>; the debug
	///     sites log at level <b>0–2</b> and ERROR is 4, so a channel at threshold <b>0</b> is fully open while
	///     <c>DebugLevel=3</c> (the fallback for every channel NOT in the table) keeps everything else quiet.
	///     <para>
	///         That is why retail's shipped file already produces exactly three live entries: <c>CallTrace=false</c>,
	///         <c>AssertMode=abort</c> and <c>PrintModule=false</c> are not settings keys, so they become channels at
	///         <c>atoi("false") == 0</c>.
	///     </para>
	///     <para>
	///         <c>DebugLevel</c> must not be lowered to open everything at once: measured, that produces ~117k lines in
	///         seconds (<c>GameCode/Movement</c> alone logs per entity per frame). Per-channel is the usable form.
	///     </para>
	///     <para>
	///         <c>TypeMode=select</c> is what puts the logger in per-channel-threshold mode (it is the call to the mode-2
	///         setter <c>FUN_004B2C90</c>); the other two values, <c>inclusive</c>/<c>exclusive</c>, are a name whitelist
	///         and blacklist with no thresholds, where our entries would not take effect. It ships as <c>select</c>, and
	///         this writer enforces it.
	///     </para>
	///     Output goes to <c>AgeOfConan.log</c> (<c>PrintFlush=true</c> flushes per line, so <c>--console</c> tails it
	///     live). Configuring the FILE rather than the live process is what makes this work on Windows as well as Linux,
	///     and it is in force from the client's first line instead of only from world entry.
	/// </remarks>
	internal static class ClientDebugChannels
	{
		// Useful and quiet: the channels that answer open questions, minus the per-frame firehoses (Movement,
		// Scry.ScriptcommandsTrace, CommandQueue, Hiding). `strings AgeOfConan.exe | grep '^GameCode/'` lists all ~90.
		public static readonly string[] Default =
		[
			// Every stat the client applies, with its previous value: "CMsg_StatChange()..(<id> <new> old:<old>)".
			// Strictly more than GameCode/Stats, which only shows server-driven sets: this also catches what the client
			// computes or derives itself, which is how the attribute pipeline is legible - the five attributes start at
			// a hardcoded 10, then each loaded item template's contribution lands as its own write, so the log
			// attributes a delta to an item id. Pairs with GameCode/ItemData, which names the template being loaded.
			"ClientInterface",
			"GameCode/Stats", // every stat the server sends, by id and value - "got new stats: 507=291000"
			"GameCode/ArmorMitigation", // TotalArmorValue / Adjusted / Min+MaxMitigation / Final Mitigation per hit
			"GameCode/ItemData", // item template load + per-item static data
			"GameCode/Magic/Calculation", // spell magnitude computation
			"GameCode/GameAction/Modify", // the magnitude after modification - "GetOnCasterModifyPercent", "Value after percent modification"
			"GameCode/Effects", // visual effect start/stop, by resource id

			// The client's weapon-damage maths, step by step, naming the stat it read at each one:
			// "CalcBaseDPS: Base DPS:2960.00" -> "DPS after abilitybonus:3868.45 (stat:804)" -> "after
			// genericdamagemodifier:4086.76". Ten lines a fight, and it is the whole formula.
			"GameCode/Combat/DPS",

			// "Tried to GetStringStat for GameActionStat_e 1241 but it did not exist" - the client naming a stat it
			// wanted and was never sent. A missing-data detector rather than a trace, and near-silent when the server
			// is right.
			"GameActionData/GetStat",

			// The world-entry / teleport handshake as a state machine ("ClientCharacterReadyToSendInPlay",
			// "TeleportEndedMessage", "Got inplay from client character <name>"), which is what turns "the client just
			// sits there" into a named step it did not get past.
			"GameCode/Inplay",

			"GameCode/Skills", // "CoreSkillHolder::AddSkill 2:<id>(<name>) 1037 from 0 to 500" - the skill baseline
			// "RECEIVED IIR: class <Name>IIR_c" - the inbound IIR trace. SLASH, not the "N3.IIR" the sink prints:
			// only `N3/IIR` exists in the exe (`strings AgeOfConan.exe | grep -xF N3/IIR`), and a name that is not a
			// real channel is simply never asked about, so it opens nothing.
			"N3/IIR"
		];

		public const string ConfName = "dbDebug.conf";

		// The client's live channel table is 0x80 entries of 48 bytes (0x02984AE0; the 0x1800 memset and the strncmp
		// width 0x30 both confirm the stride), so a longer name cannot be stored and a 0x81st channel is dropped.
		private const int MaxChannels = 0x80;
		private const int MaxNameLength = 47;

		// The settings keys FUN_004AB950 recognises (the string table at 0x01F847C4). Anything else in the file is a
		// channel, which also means a channel must not be named one of these or the parser eats the line as a setting.
		private static readonly string[] SettingKeys =
		[
			"DebugLevel", "TypeMode", "UseCallback", "ShowImportant", "Log4cxxFormat", "PrefixLevel",
			"PrintFlush", "DateStamp", "PrefixColors", "PrintType", "LogFile", "Win32Debug"
		];

		// Retail's shipped dbDebug.conf, used only if the install has none - so a missing file yields a known-good
		// config (default threshold 3, per-channel mode, per-line flush) rather than whatever the logger defaults to.
		private static readonly string[] Template =
		[
			"DebugLevel=3",
			"CallTrace=false",
			"TypeMode=select",
			"AssertMode=abort",
			"DateStamp=true",
			"PrefixLevel=3",
			"PrefixColors=false",
			"PrintModule=false",
			"PrintFlush=true",
			"PrintType=true",
			"LogFile=AgeOfConan.log",
			"Log4cxxFormat=true"
		];

		/// <summary>
		///     Writes <paramref name="channels" /> into the client's <c>dbDebug.conf</c> (in <paramref name="gameDir" />) at
		///     threshold 0, enforcing the <c>TypeMode=select</c> / <c>PrintFlush=true</c> the per-channel path needs.
		///     Idempotent: a file that already says this is left untouched. Must run BEFORE the client starts - the file is
		///     read once, during startup. Never throws; reports what it did through <paramref name="log" />.
		/// </summary>
		public static void Configure(string gameDir, IReadOnlyCollection<string> channels, Action<string> log)
		{
			var path = Path.Combine(gameDir, ConfName);

			try
			{
				var existed = File.Exists(path);

				// Build from the pristine file when the launcher has backed one up, never from the last write -
				// otherwise every run with a different --debug-channels list leaves its old entries behind and the
				// 0x80-entry table fills with channels nobody asked for. Same principle as the exe patch: transform the
				// original rather than re-transforming the output, which makes this file launcher-owned like
				// LocalConfig.xml.
				var backup = path + ".bak";
				var lines = File.Exists(backup)
					? File.ReadAllLines(backup).ToList()
					: existed
						? File.ReadAllLines(path).ToList()
						: [.. Template];

				var wanted = channels.Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.Ordinal).ToList();
				var rejected = wanted.Where(c => !IsValidName(c, out _)).ToList();
				foreach (var channel in rejected)
				{
					IsValidName(channel, out var why);
					log($"debug-log  : channel '{channel}' skipped - {why}.");
				}

				wanted = wanted.Except(rejected, StringComparer.Ordinal).ToList();

				// Everything the file already declares, keyed by name: settings stay put and channels are matched so a
				// re-run neither duplicates nor renumbers anything.
				var known = new HashSet<string>(StringComparer.Ordinal);
				var channelCount = 0;
				for (var i = 0; i < lines.Count; i++)
				{
					if (KeyOf(lines[i]) is not { } key)
						continue;

					known.Add(key);
					if (!SettingKeys.Contains(key, StringComparer.Ordinal))
						channelCount++;

					// The per-channel thresholds only exist in mode 2, and a stale inclusive/exclusive would silently
					// drop every channel we add, so rewrite the mode rather than warning about it.
					if (key == "TypeMode" && ValueOf(lines[i]) != "select")
						lines[i] = "TypeMode=select";

					// Without the per-line flush the log lags behind the client by a buffer, which makes --console
					// useless for watching a cast or a hit land.
					if (key == "PrintFlush" && ValueOf(lines[i]) != "true")
						lines[i] = "PrintFlush=true";

					// A channel we want that is already listed with a different threshold: reopen it.
					if (wanted.Contains(key, StringComparer.Ordinal) && ValueOf(lines[i]) != "0")
						lines[i] = $"{key}=0";
				}

				var added = wanted.Where(c => !known.Contains(c)).ToList();
				if (channelCount + added.Count > MaxChannels)
				{
					var room = Math.Max(0, MaxChannels - channelCount);
					log($"debug-log  : the client's channel table holds {MaxChannels} entries and {channelCount} are already " +
					    $"declared; dropping {added.Count - room} channel(s): {string.Join(", ", added.Skip(room))}");
					added = added.Take(room).ToList();
				}

				if (!known.Contains("TypeMode"))
					lines.Add("TypeMode=select");
				if (!known.Contains("PrintFlush"))
					lines.Add("PrintFlush=true");

				lines.AddRange(added.Select(c => $"{c}=0"));

				var content = string.Join(Environment.NewLine, lines) + Environment.NewLine;
				if (existed && File.ReadAllText(path) == content)
				{
					log($"debug-log  : {ConfName} already enables {wanted.Count} channel(s): {string.Join(", ", wanted)}");
					return;
				}

				File.WriteAllText(path, content);
				log($"debug-log  : {(existed ? "updated" : "created")} {ConfName} - {wanted.Count} channel(s) at threshold 0: " +
				    string.Join(", ", wanted));
				log("debug-log  : output goes to AgeOfConan.log - it is flushed per line, so --console shows it live.");
			}
			catch (Exception ex)
			{
				log($"debug-log  : failed to write {path}: {ex.Message}");
			}
		}

		// A channel name has to survive the client's tokenizer (delimiters " =\n\r"), fit a 48-byte table slot, and not
		// collide with a settings key - the parser would consume that line as the setting instead.
		private static bool IsValidName(string channel, out string why)
		{
			if (channel.Any(c => c is ' ' or '=' or '\t' || c > 0x7E || c < 0x21))
			{
				why = "the client tokenizes on \" =\\n\\r\", so a channel name cannot contain a space, '=' or non-printable ASCII";
				return false;
			}

			if (channel.Length > MaxNameLength)
			{
				why = $"it is longer than the client's {MaxNameLength}-character channel slot";
				return false;
			}

			if (channel.StartsWith('#'))
			{
				why = "a line starting with '#' is a comment to the client";
				return false;
			}

			if (SettingKeys.Contains(channel, StringComparer.Ordinal))
			{
				why = $"'{channel}' is one of the client's logger SETTINGS keys, so that line configures the logger instead of opening a channel";
				return false;
			}

			why = "";
			return true;
		}

		// The key of a `Name=Value` line, or null for a blank/comment line the client skips.
		private static string? KeyOf(string line)
		{
			var trimmed = line.Trim();
			if (trimmed.Length == 0 || trimmed.StartsWith('#'))
				return null;

			var eq = trimmed.IndexOf('=');
			var key = (eq < 0
				? trimmed
				: trimmed[..eq]).Trim();

			return key.Length == 0
				? null
				: key;
		}

		private static string ValueOf(string line)
		{
			var eq = line.IndexOf('=');
			return eq < 0
				? ""
				: line[(eq + 1)..].Trim();
		}
	}
}
