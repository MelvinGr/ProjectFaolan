using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Text;
using Faolan.Core;
using Faolan.Core.Cli;
using Faolan.Core.Crypt;
using Faolan.Core.Extensions;
using Keys = Faolan.Core.Cli.FaolanSettings.Keys;

namespace Faolan
{
	// The server's command line. One real flag - `--role` - plus the configuration keys, declared so `--help` lists
	// them and a misspelling is an error rather than a setting that silently stays on its default.
	//
	// `--role` is REPEATABLE (`--role login --role realm=2` co-hosts both in one process), which the parser handles
	// natively; it used to be a hand-rolled scan over argv precisely because the configuration binder keeps only the
	// LAST value of a repeated key.
	public static class ServerCommand
	{
		private static readonly Option<string[]> Role = new("--role")
		{
			Description = "login | realm | realm=<id>. Repeatable: `--role login --role realm=2`. Omitted = all-in-one (login + the first realm).",
			AllowMultipleArgumentsPerToken = false
		};

		// Configuration keys - the option name IS the config path, so it is spelled the same here, in faolan.json
		// and (with __) in the environment. The description and the DEFAULT come from FaolanSettings, the one catalog,
		// so `--help` cannot describe a default the code does not use.
		//
		// This is a VIEW of that catalog, not the whole of it: Login:PublicKey is launcher-side and stays off the
		// server's line, and the Cdn / ChatBridge keys belong to other
		// executables. They still get their defaults here, because the defaults layer is built from the catalog
		// rather than from this list.
		//
		// Each one is typed as the setting is (Option<ushort> for a port, Option<FaolanDbProvider> for the engine), so
		// `--Mcp:Port banana` is a parse error here rather than a crash at whatever startup first reads it - hence the
		// non-generic Option[]: this is a list of settings, not a list of strings.
		private static readonly Option[] ConfigOptions = FaolanSettings.Options(
			Keys.DatabaseProvider, Keys.DatabaseConnectionString, Keys.ServerAddress, Keys.ServerUniverseAgentPort, Keys.ServerPlayerAgentPort,
			Keys.ServerWelcomeString, Keys.ContentTerrainDir, Keys.ContentClientVersion, Keys.LoginPrivateKey, Keys.LoginAllowUnpatchedClients,
			Keys.McpEnabled, Keys.McpPort, Keys.LogFile, Keys.LogPacketBytes,
			Keys.AccountPremium, Keys.AccountPremiumCharSlots, Keys.AccountFreeCharSlots);

		// Writes a faolan.json holding every setting at its default, then exits - the `--help` list as a file you can
		// edit. It OVERWRITES what is there, which is why it is an explicit flag and nothing calls it on startup.
		private static readonly Option<string> WriteDefaultConfig = new("--write-default-config")
		{
			Description = "Write a faolan.json containing every setting at its default (to the given path, or the one in use) and exit. OVERWRITES.",
			Arity = ArgumentArity.ZeroOrOne
		};

		// Generates a fresh login DH keypair and PRINTS it - it writes nothing. The key belongs in faolan.user.json,
		// the GITIGNORED half of the config (a keypair is per-deployment and faolan.json is committed), and the
		// operator pastes it there.
		//
		// It used to write that file itself, which cost a 140-line JSON-with-comments text editor: a JsonNode
		// round-trip would have dropped every comment in the user file, so the writer edited it with regexes and then
		// re-read the result to prove the edit was valid. That is a lot of machinery, and a lot of ways to damage a
		// file, for one key going into a file the operator already has open.
		private static readonly Option<bool> GenerateLoginKey = new("--generate-login-key")
		{
			Description = "Print a new login DH keypair to paste into faolan.user.json, and exit. Every client must then be re-patched (Faolan.ClientLauncher --patch-login)."
		};

		// Returns null when the parser handled the invocation itself (--help, --write-default-config,
		// --generate-login-key, or it reported a parse error); the caller should exit with `exitCode`.
		public static ServerOptions? Parse(string[] args, out int exitCode)
		{
			var command = new RootCommand("Project Faolan - Age of Conan server protocol emulator.") { Role, WriteDefaultConfig, GenerateLoginKey };
			foreach (var option in ConfigOptions)
				command.Options.Add(option);

			if (FaolanCommandLine.Parse(command, args, out exitCode) is not { } result)
				return null;

			// ⚠️ Both flags ask whether they were GIVEN, and `GetResult(...) != null` is not that question: an
			// Option<bool> the user never mentioned still has a result - an IMPLICIT one carrying false. Asking it that
			// way meant --generate-login-key fired on EVERY run, so `dotnet run --project Faolan` printed a keypair
			// and exited instead of starting the server. A flag reads as its VALUE; --write-default-config takes an
			// optional value, so it asks for a non-implicit result (present, with or without a path).
			if (result.GetResult(WriteDefaultConfig) is { Implicit: false })
			{
				var path = result.GetValue(WriteDefaultConfig) is { Length: > 0 } given ? given : Global.ConfigPath;
				FaolanSettings.WriteDefaultJson(path);
				Console.WriteLine($"Wrote {FaolanSettings.All.Length} settings to {path}");

				return null;
			}

			if (result.GetValue(GenerateLoginKey))
			{
				GenerateKey();

				return null;
			}

			return new ServerOptions(result.GetValue(Role) ?? [], FaolanCommandLine.Overrides(result, ConfigOptions));
		}

		// BOTH halves are printed, the private one included. That reverses the old rule here ("a secret does not
		// belong in a terminal scrollback"), and it has to: nothing writes the key any more, so the operator cannot
		// paste a value they are not shown. What the old rule was protecting is also smaller than it sounds - the
		// value is one deployment's DH exponent for a private emulator, and the pair it belongs to is regenerated by
		// running this again.
		//
		// The public half is DERIVED, not stored: `ClientLoginEncryption.PublicHexFor` computes `g^serverPrivate mod N`
		// from the private key it is handed, so the two cannot drift. Printing it is not decoration - it is the value
		// every client has to be patched with.
		private static void GenerateKey()
		{
			var hex = ClientLoginEncryption.GeneratePrivate().ToString("x").TrimStart('0');;
			var text = new StringBuilder()
				.AppendLine($"A new login DH keypair. NOTHING was written - paste the private half into {Global.UserConfigPath} (gitignored):")
				.AppendLine()
				.AppendLine("\t\"Login\": {")
				.AppendLine($"\t\t\"PrivateKey\": \"{hex}\"")
				.AppendLine("\t}")
				.AppendLine()
				.AppendLine($"Its public key is {ClientLoginEncryption.PublicHexFor(HexExtensions.ToBigInteger(hex))} - derived from the private one, so the pair cannot drift.")
				.Append("⚠️  Every client must then be re-patched (Faolan.ClientLauncher --patch-login) - one that still carries the old key cannot be decrypted, and that fails SILENTLY back to accept-any.");

			Console.WriteLine(text.ToString());
		}
	}

	public sealed record ServerOptions(string[] Roles, Dictionary<string, string?> ConfigOverrides);
}
