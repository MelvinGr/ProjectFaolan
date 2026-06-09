using System.Diagnostics;
using System.Security.Cryptography;
using Faolan.Core.Crypt;

namespace Faolan.ClientLauncher
{
	// A minimal stand-in for ConanPatcher.exe: computes the two flags AgeOfConan.exe needs to run directly (instead
	// of bouncing back to the patcher) and starts the client. Both flags were recovered by RE of ConanPatcher.exe:
	//   -clienthash <md5 of AgeOfConan.exe>   the client-integrity hash the patcher forwards
	//   -key <TEA-encrypt(time||time)>        the launch-freshness token (see Faolan.Core.Crypt.ConanLaunchToken)
	// Optionally adds the auto-login flags (-username/-password/-charname) so you skip the login + character screens.
	internal static class Program
	{
		// Dev defaults (from launchClient.sh); override with the flags / env vars below.
		private const string DefaultExe =
			"/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan/AgeOfConan.exe";

		private const string DefaultProton =
			"/mnt/btrfs/@games/debian-installation/steamapps/common/Proton - Experimental/proton";

		private const string DefaultCompatData =
			"/mnt/btrfs/@games/debian-installation/steamapps/compatdata/217750";

		private const string SteamAppId = "217750";

		private static int Main(string[] args)
		{
			var exe = Environment.GetEnvironmentVariable("AOC_EXE") ?? DefaultExe;
			string? username = null, password = null, charname = null, wine = null;
			var proton = Environment.GetEnvironmentVariable("PROTON") ?? DefaultProton;
			bool dx10 = false, dryRun = false, noConsole = true;

			for (var i = 0; i < args.Length; i++)
			{
				switch (args[i])
				{
					case "--exe": exe = args[++i]; break;
					case "--username" or "-u": username = args[++i]; break;
					case "--password" or "-p": password = args[++i]; break;
					case "--charname" or "-c": charname = args[++i]; break;
					case "--wine": wine = args[++i]; break;
					case "--proton": proton = args[++i]; break;
					case "--dx10": dx10 = true; break;
					case "--console": noConsole = false; break;
					case "--dry-run" or "-n": dryRun = true; break;
					case "--help" or "-h": PrintHelp(); return 0;
					default:
						Console.Error.WriteLine($"Unknown option: {args[i]}");
						PrintHelp();
						return 2;
				}
			}

			if (dx10)
			{
				var dx10Exe = Path.Combine(Path.GetDirectoryName(exe) ?? ".", "AgeOfConanDX10.exe");
				if (File.Exists(dx10Exe))
					exe = dx10Exe;
			}

			if (!File.Exists(exe))
			{
				Console.Error.WriteLine($"Client not found: {exe}\nPass --exe <path> or set the AOC_EXE env var.");
				return 1;
			}

			// -clienthash = md5 of the exe we launch; -key = a fresh launch token.
			var clientHash = Md5Hex(exe);
			var key = ConanLaunchToken.Now();

			var launchArgs = new List<string>();
			if (noConsole) launchArgs.Add("-noconsole");
			launchArgs.AddRange(["-clienthash", clientHash, "-key", key]);
			if (username is not null) launchArgs.AddRange(["-username", username]);
			if (password is not null) launchArgs.AddRange(["-password", password]);
			if (charname is not null) launchArgs.AddRange(["-charname", charname]);

			Console.WriteLine($"exe        : {exe}");
			Console.WriteLine($"clienthash : {clientHash}");
			Console.WriteLine($"key        : {key}  (token time = {ConanLaunchToken.GetUnixTime(key)})");
			Console.WriteLine($"args       : {string.Join(' ', launchArgs)}");

			if (dryRun)
				return 0;

			var gameDir = Path.GetDirectoryName(exe)!;
			var psi = new ProcessStartInfo
			{
				WorkingDirectory = gameDir,
				UseShellExecute = false,
			};

			// Runner selection (mirrors launchClient.sh): on Windows run the exe directly; with --wine use that
			// command; otherwise launch through Proton (`proton run <exe> <args>`) with the Steam compat env.
			if (OperatingSystem.IsWindows())
			{
				psi.FileName = exe;
			}
			else if (wine is not null)
			{
				psi.FileName = wine;
				psi.ArgumentList.Add(exe);
			}
			else
			{
				psi.FileName = proton;
				psi.ArgumentList.Add("run");
				psi.ArgumentList.Add(exe);

				var home = Environment.GetEnvironmentVariable("HOME") ?? "";
				SetIfUnset(psi, "STEAM_COMPAT_DATA_PATH", DefaultCompatData);
				SetIfUnset(psi, "STEAM_COMPAT_CLIENT_INSTALL_PATH", Path.Combine(home, ".steam", "steam"));
				SetIfUnset(psi, "SteamAppId", SteamAppId);
				SetIfUnset(psi, "SteamGameId", SteamAppId);
				SetIfUnset(psi, "WINEDEBUG", "-all");
			}

			foreach (var a in launchArgs)
				psi.ArgumentList.Add(a);

			try
			{
				var proc = Process.Start(psi);
				Console.WriteLine($"launched: {psi.FileName} (pid {proc?.Id})");
				return 0;
			}
			catch (Exception e)
			{
				Console.Error.WriteLine($"launch failed: {e.Message}");
				return 1;
			}
		}

		// Set a child-process env var only if it isn't already set in the real environment (so the shell can override).
		private static void SetIfUnset(ProcessStartInfo psi, string name, string value)
		{
			if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
				psi.Environment[name] = value;
		}

		private static string Md5Hex(string path)
		{
			using var stream = File.OpenRead(path);
			return Convert.ToHexString(MD5.HashData(stream)).ToLowerInvariant();
		}

		private static void PrintHelp() =>
			Console.WriteLine(
				"""
				Faolan.ClientLauncher - launch AgeOfConan.exe directly (bypassing ConanPatcher).

				  --exe <path>        path to AgeOfConan.exe (or set AOC_EXE; defaults to the Steam path)
				  --dx10              launch AgeOfConanDX10.exe instead
				  -u, --username <s>  auto-login account name
				  -p, --password <s>  auto-login password
				  -c, --charname <s>  auto-enter this character
				  --console           keep the debug console (omit -noconsole)
				  --proton <path>     Proton 'proton' script (or set PROTON; defaults to the Steam Proton-Experimental)
				  --wine <cmd>        use this runner instead of Proton (e.g. 'wine')
				  -n, --dry-run       print the computed clienthash/key/args without launching
				  -h, --help          this help

				Off Windows it launches via Proton ('proton run <exe>') with the Steam compat env (mirroring
				launchClient.sh); pass --wine to use a plain wine runner instead. Computes -clienthash (md5 of the
				exe) and -key (TEA-encrypted timestamp; see Faolan.Core.Crypt.ConanLaunchToken). For real password
				validation also run tools/patch_aoc_login_key.py against the running client.
				""");
	}
}
