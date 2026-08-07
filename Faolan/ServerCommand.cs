using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using Faolan.Core;
using Faolan.Core.Cli;
using Faolan.Core.Crypt;

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
		// This is a VIEW of that catalog, not the whole of it: Login:PublicKey / Login:RetailPublicKey are
		// launcher-side and stay off the server's line, and the CdnMirror / ChatBridge keys belong to other
		// executables. They still get their defaults here, because the defaults layer is built from the catalog
		// rather than from this list.
		private static readonly Option<string>[] ConfigOptions = FaolanSettings.Options(
			"Database:Provider", "Database:ConnectionString", "ServerIpAddress", "UniverseAgentPort", "PlayerAgentPort", "WelcomeString", "TerrainDir",
			"ClientVersion", "Login:PrivateKey", "Login:AllowUnpatchedClients", "Mcp:Enabled", "Mcp:Port", "Mcp:Url", "Log:File", "Log:PacketBytes",
			"Account:Premium", "Account:PremiumCharSlots", "Account:FreeCharSlots");

		// Writes a faolan.json holding every setting at its default, then exits - the `--help` list as a file you can
		// edit. It OVERWRITES what is there, which is why it is an explicit flag and nothing calls it on startup.
		private static readonly Option<string> WriteDefaultConfig = new("--write-default-config")
		{
			Description = "Write a faolan.json containing every setting at its default (to the given path, or the one in use) and exit. OVERWRITES.",
			Arity = ArgumentArity.ZeroOrOne
		};

		// Generates a fresh login DH private key and writes it to faolan.user.json - the GITIGNORED half of the config,
		// because a key is per-deployment and faolan.json is committed.
		private static readonly Option<string> GenerateLoginKey = new("--generate-login-key")
		{
			Description = "Generate a new login DH private key into faolan.user.json (or the given path) and exit. Every client must then be re-patched (Faolan.ClientLauncher --patch-login).",
			Arity = ArgumentArity.ZeroOrOne
		};

		private static readonly Option<bool> Force = new("--force") { Description = "Let --generate-login-key replace a Login:PrivateKey that is already set." };

		// Returns null when the parser handled the invocation itself (--help, --write-default-config,
		// --generate-login-key, or it reported a parse error); the caller should exit with `exitCode`.
		public static ServerOptions? Parse(string[] args, out int exitCode)
		{
			var command = new RootCommand("Project Faolan - Age of Conan server protocol emulator.") { Role, WriteDefaultConfig, GenerateLoginKey, Force };
			foreach (var option in ConfigOptions)
				command.Options.Add(option);

			if (FaolanCommandLine.Parse(command, args, out exitCode) is not { } result)
				return null;

			if (result.GetResult(WriteDefaultConfig) != null)
			{
				var path = result.GetValue(WriteDefaultConfig) is { Length: > 0 } given ? given : Global.ConfigPath;
				FaolanSettings.WriteDefaultJson(path);
				Console.WriteLine($"Wrote {FaolanSettings.All.Length} settings to {path}");

				return null;
			}

			if (result.GetResult(GenerateLoginKey) != null)
			{
				exitCode = GenerateKey(result.GetValue(GenerateLoginKey), result.GetValue(Force));

				return null;
			}

			return new ServerOptions(result.GetValue(Role) ?? [], FaolanCommandLine.Overrides(result, ConfigOptions));
		}

		// The private key is written, never PRINTED - it is the one value in this config that is a secret, and a
		// terminal scrollback is not where it belongs. The derived public half is printed instead, because that is
		// what has to end up in every client and what tells you the write took effect.
		private static int GenerateKey(string? given, bool force)
		{
			// Default to the user config, because that is where a per-deployment secret belongs; a path is accepted for
			// the deployment that keeps its config somewhere else, the same as --write-default-config.
			var path = given is { Length: > 0 } ? given : Global.UserConfigPath;
			try
			{
				var hex = DhKeys.GeneratePrivateHex();
				UserConfigWriter.Set(path, "Login:PrivateKey", hex, force);
				DhKeys.Configure(hex);
			}
			catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException)
			{
				Console.Error.WriteLine(e.Message);

				return 1;
			}

			Console.WriteLine($"Wrote a new Login:PrivateKey to {path}");
			Console.WriteLine($"Its public key is {DhKeys.ServerPublicHex}");
			Console.WriteLine("⚠️  Every client must now be re-patched (Faolan.ClientLauncher --patch-login) - one that still carries the old key cannot be decrypted, and that fails SILENTLY back to accept-any.");

			return 0;
		}
	}

	public sealed record ServerOptions(string[] Roles, Dictionary<string, string?> ConfigOverrides);
}
