using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.CommandLine;
using Faolan.Core.Cli;

namespace Faolan.ClientLauncher
{
	// The launcher's command line, declared once (System.CommandLine) rather than scanned by hand.
	//
	// What the declaration buys over the old switch over argv: `--help` is GENERATED from the option descriptions
	// rather than hand-maintained beside them (the old block never mentioned `--debug-channels` at all), a missing
	// value is an error the parser words rather than three copies of the same `i + 1 >= args.Length` guard - and one
	// the parser applies to EVERY option, not just the three that happened to carry a guard - and `--exe`,
	// `--username`, ... no longer read a value past the end of argv and crash with an IndexOutOfRangeException.
	//
	// Two kinds of option live here, as everywhere else in the repo:
	//   - CONFIG KEYS (Login:PrivateKey, Login:PublicKey, Login:RetailPublicKey) name a setting that also lives in
	//     faolan.json and the environment, so the option's NAME is the configuration path and its value is resolved
	//     through the shared builder (FaolanCommandLine) - the launcher patches the key the server is holding
	//     without a second setting to keep in step.
	//   - COMMAND options (--exe, --console, --restore, ...) belong to this invocation alone. Their short aliases
	//     (-u/-p/-c/-n) stay: unlike a config key, these have no second spelling in a file or the environment to
	//     drift against, and they are what the documented test launches type.
	public static class LauncherCommand
	{
		private static readonly Option<string> Cdn = new("--cdn")
		{
			Description = "Point LocalConfig.xml's three CDN keys (HttpPatchAddr/ClientHttpPatchAddr/ControlHttpPatchAddr) at this base URL - each becomes <url>/<tag>/upm."
		};

		private static readonly Option<string> Charname = new("--charname", "-c") { Description = "Auto-enter this character." };

		private static readonly Option<bool> ConsoleOption = new("--console")
		{
			Description = "Run in the foreground and tail AgeOfConan.log into THIS terminal (the same content as the client console, no separate wineconsole window); also drops -noconsole."
		};

		// Optional value: `--debug-channels a,b` takes that list, a bare `--debug-channels` takes the default set.
		// Zero-or-one arity is what tells the two apart - a bare flag is PRESENT with no value, which is why the
		// reader below asks whether the option was GIVEN rather than whether its value is null.
		private static readonly Option<string> DebugChannels = new("--debug-channels")
		{
			Description = "Enable the client's OWN debug channels by writing its logger config file dbDebug.conf (no exe change, no process memory): a comma-separated list, or the default low-noise set if given bare. Implies --console. Full list: strings AgeOfConan.exe | grep '^GameCode/'",
			Arity = ArgumentArity.ZeroOrOne,
			HelpName = "a,b"
		};

		private static readonly Option<bool> DryRun = new("--dry-run", "-n") { Description = "Print the computed clienthash/key/args without launching." };
		private static readonly Option<bool> Dx10 = new("--dx10") { Description = "Launch AgeOfConanDX10.exe instead." };
		private static readonly Option<string> Exe = new("--exe") { Description = "Path to AgeOfConan.exe (default: auto-detected from your Steam install, app 217750)." };

		private static readonly Option<string> LauncherNotes = new("--launcher-notes")
		{
			Description = "Redirect the client's hardcoded launcher-notes URL (retail's msgs.ageofconan.com is dead) to this one - keep the two %s (UniverseName, Language)."
		};

		// The DH keys, named EXACTLY as they are in faolan.json and the environment (the one-name-per-setting rule -
		// see Faolan.Core/Cli/FaolanCommandLine.cs). Passing them here is only for a one-off patch; the normal path is
		// that the launcher reads the same faolan.json the server does, so a configured Login:PrivateKey automatically
		// patches its own matching public key and the pair cannot drift.
		private static readonly Option<string>[] LoginKeys = FaolanSettings.Options("Login:PrivateKey", "Login:PublicKey", "Login:RetailPublicKey");

		private static readonly Option<bool> PatchLogin = new("--patch-login")
		{
			Description = "Patch the client's DH key ON DISK before launch so the server can validate the real password. The key comes from the SAME faolan.json/environment the server reads, so overriding Login:PrivateKey there patches the matching public key automatically."
		};

		private static readonly Option<string> Password = new("--password", "-p") { Description = "Auto-login password." };

		private static readonly Option<string> Proton = new("--proton")
		{
			Description = "(Linux only) Proton 'proton' script, or set PROTON (default: auto-detected - the build Steam runs AoC with). Ignored on Windows, which runs the exe directly."
		};

		private static readonly Option<bool> Restore = new("--restore")
		{
			Description = "Restore AgeOfConan.exe / AgeOfConanDX10.exe / LocalConfig.xml / dbDebug.conf from their .bak (undoes the login/notes exe patch, the CDN/UniverseAddr redirects and the debug channels) and exit."
		};

		private static readonly Option<string> UniverseAddr = new("--universeaddr") { Description = "Point LocalConfig.xml <UniverseAddr> (the login server) at this host:port." };
		private static readonly Option<string> Username = new("--username", "-u") { Description = "Auto-login account name." };
		private static readonly Option<string> Wine = new("--wine") { Description = "(Linux only) use this runner instead of Proton (e.g. 'wine'). Ignored on Windows." };

		// The --launcher-notes URL has to satisfy the client's fixed buffer and its SSL-less libcurl, and a URL that
		// does not is a typo in the flag rather than a runtime condition - so it is refused HERE, worded by the same
		// validator the patcher applies, instead of after the exe has been backed up and half-patched.
		static LauncherCommand()
		{
			LauncherNotes.Validators.Add(result =>
			{
				if (result.GetValueOrDefault<string>() is { } url && !ClientExePatcher.ValidateNotesUrl(url, out var error))
					result.AddError($"--launcher-notes URL invalid: {error}.");
			});
		}

		// Parses argv. Returns null when the parser handled the invocation itself (--help, or a parse error, which it
		// reports); the exit code is then in `exitCode`.
		public static LauncherOptions? Parse(string[] args, out int exitCode)
		{
			var command = new RootCommand(
				"Faolan.ClientLauncher - launch AgeOfConan.exe directly, bypassing ConanPatcher. Computes the two flags the client's launch gate needs " +
				"(-clienthash, the md5 of the exe it launches, and -key, a TEA-encrypted timestamp - see Faolan.Core.Crypt.ConanLaunchToken), kills a " +
				"client already running from the SAME install so repeated test launches start clean, and off Windows launches through Proton " +
				"('proton run <exe>') with the Steam compat env. The exe stays GUI-subsystem, so no separate wineconsole window is spawned. Every " +
				"LocalConfig.xml/exe redirect it applies PERSISTS after the run; --restore undoes them.")
			{
				Exe, Dx10, Username, Password, Charname,
				UniverseAddr, Cdn, LauncherNotes, PatchLogin,
				Restore, ConsoleOption, DebugChannels, Proton, Wine, DryRun
			};

			foreach (var key in LoginKeys)
				command.Options.Add(key);

			if (FaolanCommandLine.Parse(command, args, out exitCode) is not { } result)
				return null;

			// --debug-channels implies --console: the point of opening the channels is to read what they print, so the
			// two flags resolve to ONE decision here rather than being re-derived at each of its use sites.
			var channels = DebugChannelsOf(result);
			return new LauncherOptions(
				result.GetValue(Exe), result.GetValue(Dx10),
				result.GetValue(Username), result.GetValue(Password), result.GetValue(Charname),
				result.GetValue(UniverseAddr), result.GetValue(Cdn), result.GetValue(LauncherNotes),
				result.GetValue(PatchLogin), result.GetValue(Restore),
				result.GetValue(ConsoleOption) || channels.Length > 0, channels,
				result.GetValue(Proton), result.GetValue(Wine), result.GetValue(DryRun),
				FaolanCommandLine.Overrides(result, LoginKeys));
		}

		// Empty when --debug-channels was not given; the default set when it was given bare; the listed channels when
		// it carried a value.
		private static ImmutableArray<string> DebugChannelsOf(ParseResult result)
		{
			if (result.GetResult(DebugChannels) == null)
				return [];

			return result.GetValue(DebugChannels) is { Length: > 0 } list
				? [.. list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
				: [.. ClientDebugChannels.Default];
		}
	}

	// Everything the launcher was scraping out of argv, parsed once.
	public sealed record LauncherOptions(
		string? Exe, bool Dx10,
		string? Username, string? Password, string? Charname,
		string? UniverseAddr, string? Cdn, string? LauncherNotes,
		bool PatchLogin, bool Restore, bool Console, ImmutableArray<string> DebugChannels,
		string? Proton, string? Wine, bool DryRun,
		Dictionary<string, string?> LoginKeys);
}
