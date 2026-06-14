using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
		private const string DefaultCompatData = "/mnt/btrfs/@games/debian-installation/steamapps/compatdata/217750";

		// Dev defaults (from launchClient.sh); override with the flags / env vars below.
		private const string DefaultExe = "/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan/AgeOfConan.exe";

		private const string DefaultProton = "/mnt/btrfs/@games/debian-installation/steamapps/common/Proton - Experimental/proton";

		private const string SteamAppId = "217750";

		private static bool ClientIsRunning()
		{
			if (OperatingSystem.IsWindows())
			{
				return Process.GetProcessesByName("AgeOfConan")
					.Length > 0 || Process.GetProcessesByName("AgeOfConanDX10")
					.Length > 0;
			}

			foreach (var dir in Directory.EnumerateDirectories("/proc"))
			{
				if (!int.TryParse(Path.GetFileName(dir), out _))
					continue;

				try
				{
					if (File
					    .ReadAllText(Path.Combine(dir, "comm"))
					    .Trim()
					    .StartsWith("AgeOfConan", StringComparison.Ordinal))
						return true;
				}
				catch
				{
					// vanished/unreadable
				}
			}

			return false;
		}

		// Print any AgeOfConan.log content past `pos`; returns the new position. Resets to 0 if the file shrank
		// (a new session truncated/recreated it). Shared-read so the client can keep writing/rotating it.
		private static long DumpNewLogContent(string path, long pos)
		{
			try
			{
				if (!File.Exists(path))
					return pos;

				using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
				if (fs.Length < pos)
					pos = 0;

				fs.Seek(pos, SeekOrigin.Begin);
				using var reader = new StreamReader(fs);
				var text = reader.ReadToEnd();
				if (text.Length > 0)
					Console.Write(text);

				return fs.Position;
			}
			catch
			{
				return pos;
			}
		}

		// Kill any running AgeOfConan client. On Windows that's the named process; under Wine/Proton it's a Linux
		// process whose /proc comm is the exe name (or whose cmdline references it - excluding this launcher).
		private static void KillRunningClients()
		{
			var killed = 0;

			if (OperatingSystem.IsWindows())
			{
				foreach (var name in new[] { "AgeOfConan", "AgeOfConanDX10" })
				foreach (var p in Process.GetProcessesByName(name))
					killed += TryKill(p.Id);
			}
			else
			{
				var self = Environment.ProcessId;
				foreach (var dir in Directory.EnumerateDirectories("/proc"))
				{
					if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid == self)
						continue;

					try
					{
						var comm = File
							.ReadAllText(Path.Combine(dir, "comm"))
							.Trim();
						var isClient = comm.StartsWith("AgeOfConan", StringComparison.Ordinal);
						if (!isClient)
						{
							var cmdline = File
								.ReadAllText(Path.Combine(dir, "cmdline"))
								.Replace('\0', ' ');
							isClient = cmdline.Contains("AgeOfConan.exe", StringComparison.Ordinal) && !cmdline.Contains("Faolan.ClientLauncher", StringComparison.Ordinal) && comm != "dotnet";
						}

						if (isClient)
							killed += TryKill(pid);
					}
					catch
					{
						// process vanished or unreadable; ignore.
					}
				}
			}

			if (killed > 0)
				Console.WriteLine($"killed {killed} running client instance(s)");
		}

		private static int Main(string[] args)
		{
			var exe = Environment.GetEnvironmentVariable("AOC_EXE") ?? DefaultExe;
			string? username = null, password = null, charname = null, wine = null, universeAddr = null;
			var proton = Environment.GetEnvironmentVariable("PROTON") ?? DefaultProton;
			bool dx10 = false, dryRun = false, noConsole = true, streamOutput = false, patchLogin = false, detach = false, restore = false;

			for (var i = 0; i < args.Length; i++)
			{
				switch (args[i])
				{
					case "--exe":
						exe = args[++i];
						break;
					case "--username" or "-u":
						username = args[++i];
						break;
					case "--password" or "-p":
						password = args[++i];
						break;
					case "--charname" or "-c":
						charname = args[++i];
						break;
					case "--wine":
						wine = args[++i];
						break;
					case "--proton":
						proton = args[++i];
						break;
					case "--dx10":
						dx10 = true;
						break;
					// Point LocalConfig.xml <UniverseAddr> at our server for this session (reverted on exit). Bare
					// flag defaults to Faolan's 127.0.0.1:7010; replaces the need for an /etc/hosts override.
					case "--universeaddr":
						universeAddr = i + 1 < args.Length && !args[i + 1]
							.StartsWith('-')
							? args[++i]
							: "127.0.0.1:7010";
						break;
					// Run in the foreground and stream the client's stdout/stderr into THIS terminal (no separate
					// wineconsole window). Also drops -noconsole so the client doesn't suppress its own logging.
					case "--console":
						noConsole = false;
						streamOutput = true;
						break;
					case "--patch-login":
						patchLogin = true;
						break;
					// Fire-and-forget: with --universeaddr, revert the config shortly after the client has read it
					// and return, instead of holding the foreground until the client exits.
					case "--detach":
						detach = true;
						break;
					case "--restore":
						restore = true;
						break;
					case "--dry-run" or "-n":
						dryRun = true;
						break;
					case "--help" or "-h":
						PrintHelp();
						return 0;
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

			// --restore: just heal a UniverseAddr left redirected by a hard-killed run (via the sidecar) and exit.
			if (restore)
			{
				RecoverUniverseAddr(Path.GetDirectoryName(exe)!);
				Console.WriteLine("restore done.");
				return 0;
			}

			// Keep the exe as a GUI subsystem app: a CUI flip makes Wine spawn a separate wineconsole window, which
			// we don't want - we stream the client's stdio into this terminal instead (see --console below). This
			// also undoes any earlier CUI patch. Done before the md5 so -clienthash matches the launched file.
			if (!dryRun)
				SetConsoleSubsystem(exe, false);

			// -clienthash = md5 of the exe we launch; -key = a fresh launch token.
			var clientHash = Md5Hex(exe);
			var key = ConanLaunchToken.Now();

			var launchArgs = new List<string>();
			if (noConsole)
				launchArgs.Add("-noconsole");
			launchArgs.AddRange(["-clienthash", clientHash, "-key", key]);
			if (username is not null)
				launchArgs.AddRange(["-username", username]);
			if (password is not null)
				launchArgs.AddRange(["-password", password]);
			if (charname is not null)
				launchArgs.AddRange(["-charname", charname]);

			Console.WriteLine($"exe        : {exe}");
			Console.WriteLine($"clienthash : {clientHash}");
			Console.WriteLine($"key        : {key}  (token time = {ConanLaunchToken.GetUnixTime(key)})");
			Console.WriteLine($"args       : {string.Join(' ', launchArgs)}");

			if (dryRun)
				return 0;

			// Kill any client already running first (so repeated test launches start clean).
			KillRunningClients();

			var gameDir = Path.GetDirectoryName(exe)!;

			// Heal a UniverseAddr left redirected by a previous run that was hard-killed before it could revert
			// (SIGKILL is uncatchable; `dotnet run` may not forward signals). The sidecar holds the real value.
			RecoverUniverseAddr(gameDir);

			// Point the client's login server (LocalConfig.xml <UniverseAddr>) at ours for this session. The
			// original value is written to a sidecar first so the redirect survives a crash (next run heals it),
			// and reverted after the client exits (and on Ctrl+C / process exit).
			var originalUniverseAddr = universeAddr is null
				? null
				: SetUniverseAddr(gameDir, universeAddr);
			if (originalUniverseAddr is not null)
			{
				File.WriteAllText(SidecarPath(gameDir), originalUniverseAddr);
				var revertTo = originalUniverseAddr;

				void Revert()
				{
					SetUniverseAddr(gameDir, revertTo);
					try
					{
						File.Delete(SidecarPath(gameDir));
					}
					catch
					{
						/* ignore */
					}
				}

				Console.CancelKeyPress += (_, _) => Revert();
				AppDomain.CurrentDomain.ProcessExit += (_, _) => Revert();
			}

			var psi = new ProcessStartInfo
			{
				WorkingDirectory = gameDir,
				UseShellExecute = false
			};

			// Runner selection (mirrors launchClient.sh): on Windows run the exe directly; with --wine use that
			// command; otherwise launch through Proton (`proton run <exe> <args>`) with the Steam compat env.
			if (OperatingSystem.IsWindows())
				psi.FileName = exe;
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

				SetIfUnset(psi, "PROTON_USE_WINED3D", "1");
				SetIfUnset(psi, "PROTON_NO_ESYNC", "1");
			}

			foreach (var a in launchArgs)
				psi.ArgumentList.Add(a);

			// The client writes its console output (WriteConsoleW) to a Wine console - lost when we don't flip to
			// CUI - but the SAME content also goes to AgeOfConan.log. So --console tails that log into this terminal
			// (proton's own messages still inherit our stdio). Record the log's pre-launch size so we only show this
			// session (the client truncates/rotates it on start).
			var logPath = Path.Combine(gameDir, "AgeOfConan.log");
			var logStart = streamOutput && File.Exists(logPath)
				? new FileInfo(logPath).Length
				: 0;

			Process proc;
			try
			{
				proc = Process.Start(psi)!;
				Console.WriteLine($"launched: {psi.FileName} (pid {proc.Id})");
			}
			catch (Exception e)
			{
				Console.Error.WriteLine($"launch failed: {e.Message}");
				return 1;
			}

			// Patch the client's embedded DH pubkey (Funcom -> CELLAO) in memory so the server can validate the
			// password (the in-launcher port of tools/patch_aoc_login_key.py). Runs alongside the client; needs
			// ptrace permission (sudo if it can't); harmless if skipped.
			if (patchLogin && !OperatingSystem.IsWindows())
				Task.Run(() => LoginKeyPatcher.PatchWhenReady());

			// Stay in the foreground (tailing the log and/or holding the UniverseAddr override) until the client
			// exits, then revert the config. Without either, launch detached and return.
			try
			{
				if (streamOutput)
				{
					Console.WriteLine($"tailing {logPath} (Ctrl+C to stop) ...");
					var pos = logStart;
					while (!proc.HasExited)
					{
						pos = DumpNewLogContent(logPath, pos);
						Thread.Sleep(200);
					}

					DumpNewLogContent(logPath, pos);
				}
				else if (originalUniverseAddr is not null && detach)
				{
					// Wait only until the client has read LocalConfig, then fall through to revert and return -
					// the client keeps running.
					WaitForClientStartup();
				}
				else if (originalUniverseAddr is not null)
					proc.WaitForExit();
			}
			finally
			{
				if (originalUniverseAddr is not null)
				{
					SetUniverseAddr(gameDir, originalUniverseAddr);
					try
					{
						File.Delete(SidecarPath(gameDir));
					}
					catch
					{
						/* ignore */
					}

					Console.WriteLine($"reverted UniverseAddr -> {originalUniverseAddr}");
				}
			}

			return proc.HasExited
				? proc.ExitCode
				: 0;
		}

		private static string Md5Hex(string path)
		{
			using var stream = File.OpenRead(path);
			return Convert
				.ToHexString(MD5.HashData(stream))
				.ToLowerInvariant();
		}

		private static void PrintHelp()
		{
			Console.WriteLine("""
			                  Faolan.ClientLauncher - launch AgeOfConan.exe directly (bypassing ConanPatcher).

			                    --exe <path>        path to AgeOfConan.exe (or set AOC_EXE; defaults to the Steam path)
			                    --dx10              launch AgeOfConanDX10.exe instead
			                    -u, --username <s>  auto-login account name
			                    -p, --password <s>  auto-login password
			                    -c, --charname <s>  auto-enter this character
			                    --universeaddr [a]  point LocalConfig.xml <UniverseAddr> (the login server) at `a` for this
			                                        session, reverted on exit; bare flag = 127.0.0.1:7010 (replaces /etc/hosts)
			                    --detach            with --universeaddr: revert + return once the client has read the config,
			                                        instead of holding the foreground until the client exits
			                    --restore           heal a UniverseAddr left redirected by a hard-killed run (via the sidecar), exit
			                    --console           run in the foreground and tail AgeOfConan.log into THIS terminal (same content
			                                        as the client console, no separate wineconsole window); also drops -noconsole
			                    --patch-login       patch the client's DH key (Funcom -> CELLAO) in memory after launch so the
			                                        server can validate the real password (needs ptrace permission; sudo if it can't)
			                    --proton <path>     Proton 'proton' script (or set PROTON; defaults to the Steam Proton-Experimental)
			                    --wine <cmd>        use this runner instead of Proton (e.g. 'wine')
			                    -n, --dry-run       print the computed clienthash/key/args without launching
			                    -h, --help          this help

			                  --console keeps the client in the foreground and tails AgeOfConan.log here (its console output also
			                  goes to that file). The exe stays GUI-subsystem so no separate wineconsole window is spawned.
			                  Kills any already-running client first (clean test launches). Off Windows it launches via Proton
			                  ('proton run <exe>') with the Steam compat env (mirroring launchClient.sh); pass --wine to use a
			                  plain wine runner instead. Computes -clienthash (md5 of the exe) and -key (TEA-encrypted timestamp;
			                  see Faolan.Core.Crypt.ConanLaunchToken).
			                  """);
		}

		// If a prior run left the sidecar (redirect not reverted), restore UniverseAddr from it and remove it.
		private static void RecoverUniverseAddr(string gameDir)
		{
			var sidecar = SidecarPath(gameDir);
			if (!File.Exists(sidecar))
				return;

			try
			{
				var saved = File
					.ReadAllText(sidecar)
					.Trim();
				if (saved.Length > 0)
				{
					SetUniverseAddr(gameDir, saved);
					Console.WriteLine($"recovered stale UniverseAddr redirect -> {saved}");
				}

				File.Delete(sidecar);
			}
			catch (Exception e)
			{
				Console.Error.WriteLine($"UniverseAddr recovery failed: {e.Message}");
			}
		}

		// Flip the PE subsystem byte: GUI(2) <-> CUI/console(3). The offset is computed from the PE header (no magic
		// constant); only writes when it differs and the current value is a sane 2/3 (so we never corrupt the exe).
		private static void SetConsoleSubsystem(string exe, bool console)
		{
			try
			{
				using var fs = new FileStream(exe, FileMode.Open, FileAccess.ReadWrite);
				using var br = new BinaryReader(fs, Encoding.ASCII, true);

				fs.Position = 0x3c;
				var peOffset = br.ReadInt32();
				fs.Position = peOffset;
				if (br.ReadUInt32() != 0x0000_4550) // "PE\0\0"
					return;

				var subsystemOffset = peOffset + 4 + 20 + 68; // PE sig + COFF header + Subsystem offset in optional header
				fs.Position = subsystemOffset;
				var current = br.ReadByte();
				var want = (byte)(console
					? 3
					: 2);

				if (current is not (2 or 3) || current == want)
					return;

				fs.Position = subsystemOffset;
				fs.WriteByte(want);
				Console.WriteLine($"patched PE subsystem -> {(console ? "CUI (console)" : "GUI")}");
			}
			catch (Exception e)
			{
				Console.Error.WriteLine($"subsystem patch skipped: {e.Message}");
			}
		}

		// Set a child-process env var only if it isn't already set in the real environment (so the shell can override).
		private static void SetIfUnset(ProcessStartInfo psi, string name, string value)
		{
			if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
				psi.Environment[name] = value;
		}

		// Set LocalConfig.xml's <UniverseAddr> (the client's login server) to `addr`, returning the previous value
		// (so the caller can revert), or null if nothing changed / the file or field is missing. Re-enables a
		// LocalConfig.xml.disabled if that's all that's present.
		private static string? SetUniverseAddr(string gameDir, string addr)
		{
			try
			{
				var path = Path.Combine(gameDir, "LocalConfig.xml");
				if (!File.Exists(path))
				{
					var disabled = path + ".disabled";
					if (!File.Exists(disabled))
					{
						Console.Error.WriteLine("UniverseAddr: LocalConfig.xml not found; skipped.");
						return null;
					}

					File.Move(disabled, path);
				}

				var text = File.ReadAllText(path);
				var match = Regex.Match(text, "(<UniverseAddr>)(.*?)(</UniverseAddr>)");
				if (!match.Success)
				{
					Console.Error.WriteLine("UniverseAddr: no <UniverseAddr> in LocalConfig.xml; skipped.");
					return null;
				}

				var current = match.Groups[2].Value;
				if (current == addr)
					return null;

				File.WriteAllText(path, Regex.Replace(text, "(<UniverseAddr>)(.*?)(</UniverseAddr>)", m => m.Groups[1].Value + addr + m.Groups[3].Value));
				Console.WriteLine($"UniverseAddr: {current} -> {addr}");
				return current;
			}
			catch (Exception e)
			{
				Console.Error.WriteLine($"UniverseAddr update failed: {e.Message}");
				return null;
			}
		}

		// Sidecar holding the real <UniverseAddr> while we've redirected it, so a hard-killed run can be healed.
		private static string SidecarPath(string gameDir)
		{
			return Path.Combine(gameDir, "LocalConfig.xml.faolan-revert");
		}

		private static int TryKill(int pid)
		{
			try
			{
				using var p = Process.GetProcessById(pid);
				p.Kill(true);
				return 1;
			}
			catch
			{
				return 0;
			}
		}

		// For --detach: wait until the client process is up (it reads LocalConfig at startup) plus a grace window,
		// so we don't revert UniverseAddr before the client has read it. Proton cold-start can be slow, hence the
		// generous timeout + grace.
		private static void WaitForClientStartup()
		{
			var deadline = Environment.TickCount64 + 60_000;
			while (Environment.TickCount64 < deadline && !ClientIsRunning())
				Thread.Sleep(250);

			Thread.Sleep(12_000); // grace for the engine to read UniverseAddr after the process appears
		}
	}
}
