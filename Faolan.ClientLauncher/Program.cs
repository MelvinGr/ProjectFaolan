using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
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
		private const string SteamAppId = "217750";

		// Copy path -> path.bak only if the backup doesn't already exist, so the FIRST run captures the pristine
		// original and later runs (which see an already-modified file) never overwrite it.
		private static void BackupOnce(string path)
		{
			var bak = path + ".bak";
			if (File.Exists(path) && !File.Exists(bak))
				File.Copy(path, bak);
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

		// A normalized, case-insensitive fragment of the exe path used to recognise a running client from the SAME
		// install in its command line. The path is compared below its root (leading separator dropped, backslashes
		// unified to '/', lowercased) so both the native "/mnt/.../AgeOfConan.exe" and the Wine "Z:\mnt\...\AgeOfConan.exe"
		// forms match the same install while a different install's path does not.
		private static string ExePathSignature(string exe)
		{
			var full = exe;
			try
			{
				full = Path.GetFullPath(exe);
			}
			catch
			{
				// keep the raw value if it can't be resolved
			}

			return NormalizePath(full).TrimStart('/');
		}

		// Kill the running AgeOfConan client launched from THIS `exe` path only (so a client running from a different
		// install is left alone) - matched by the exe's full path in the process command line, NOT just the
		// "AgeOfConan.exe" name. Under Wine/Proton the path appears in the Z:\ drive form, so we compare on a normalized
		// signature (see ExePathSignature). On Windows we compare the process main-module path.
		private static void KillRunningClients(string exe)
		{
			var killed = 0;
			var sig = ExePathSignature(exe);

			if (OperatingSystem.IsWindows())
			{
				foreach (var name in new[] { "AgeOfConan", "AgeOfConanDX10" })
				foreach (var p in Process.GetProcessesByName(name))
				{
					try
					{
						if (NormalizePath(p.MainModule?.FileName ?? "").Contains(sig, StringComparison.Ordinal))
							killed += TryKill(p.Id);
					}
					catch
					{
						// MainModule can throw (access denied / bitness mismatch / vanished); skip.
					}
				}
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
						var comm = File.ReadAllText(Path.Combine(dir, "comm")).Trim();
						var cmdline = File.ReadAllText(Path.Combine(dir, "cmdline")).Replace('\0', ' ');
						// Only this install's client: its cmdline must reference the exact exe PATH (not just the
						// AgeOfConan.exe name), excluding this launcher (which carries the path in its own --exe args).
						var isClient = NormalizePath(cmdline).Contains(sig, StringComparison.Ordinal) && !cmdline.Contains("Faolan.ClientLauncher", StringComparison.Ordinal) && comm != "dotnet";

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
			string? exe = null; // --exe overrides; else Steam auto-detect (SteamPaths) after the arg loop
			string? username = null;
			string? password = null;
			string? charname = null;
			string? wine = null;
			string? universeAddr = null;
			string? proton = null; // --proton/PROTON overrides; else Steam auto-detect (SteamPaths) at launch
			var dx10 = false;
			var dryRun = false;
			var noConsole = true;
			var streamOutput = false;
			var patchLogin = false;
			var restore = false;
			string? cdnUrl = null;
			string? launcherNotes = null;

			for (var i = 0; i < args.Length; i++)
			{
				switch (args[i])
				{
					case "--exe":
					{
						exe = args[++i];
						break;
					}

					case "--username" or "-u":
					{
						username = args[++i];
						break;
					}

					case "--password" or "-p":
					{
						password = args[++i];
						break;
					}

					case "--charname" or "-c":
					{
						charname = args[++i];
						break;
					}

					// --wine / --proton pick the LINUX runner for the Windows client (Proton by default, or a plain wine
					// command). Both are ignored on Windows, where the exe runs directly (see the runner selection below).
					case "--wine":
					{
						wine = args[++i];
						break;
					}

					case "--proton":
					{
						proton = args[++i];
						break;
					}

					case "--dx10":
					{
						dx10 = true;
						break;
					}

					// Redirect LocalConfig.xml <UniverseAddr> (the login server) to `a`. Required value, no default.
					case "--universeaddr":
					{
						if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
						{
							Console.Error.WriteLine("--universeaddr requires an address (e.g. --universeaddr <host:port>).");
							return 2;
						}

						universeAddr = args[++i];
						break;
					}

					// Run in the foreground and stream the client's stdout/stderr into THIS terminal (no separate
					// wineconsole window). Also drops -noconsole so the client doesn't suppress its own logging.
					case "--console":
					{
						noConsole = false;
						streamOutput = true;
						break;
					}

					// Redirect LocalConfig.xml's three CDN keys to the mirror base URL (each -> <base>/<tag>/upm) so
					// loading-screen content pulls hit the store-and-forward mirror. Required value, no default.
					case "--cdn":
					{
						if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
						{
							Console.Error.WriteLine("--cdn requires a base URL (e.g. --cdn http://host:port).");
							return 2;
						}

						cdnUrl = args[++i];
						break;
					}

					// Redirect the client's hardcoded launcher-notes URL (retail's msgs.ageofconan.com is dead). Required
					// value, no default; keep the two %s (UniverseName, Language).
					case "--launcher-notes":
					{
						if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
						{
							Console.Error.WriteLine("--launcher-notes requires a URL (keep the two %s, e.g. http://host/notes.php?UniverseName=%s&Language=%s).");
							return 2;
						}

						launcherNotes = args[++i];
						if (!ClientExePatcher.ValidateNotesUrl(launcherNotes, out var notesErr))
						{
							Console.Error.WriteLine($"--launcher-notes URL invalid: {notesErr}.");
							return 2;
						}

						break;
					}

					case "--patch-login":
					{
						patchLogin = true;
						break;
					}

					case "--restore":
					{
						restore = true;
						break;
					}

					case "--dry-run" or "-n":
					{
						dryRun = true;
						break;
					}

					case "--help" or "-h":
					{
						PrintHelp();
						return 0;
					}

					default:
					{
						Console.Error.WriteLine($"Unknown option: {args[i]}");
						PrintHelp();
						return 2;
					}
				}
			}

			// Resolve the client exe: --exe wins, else auto-detect the Steam install (app 217750, Linux + Windows).
			// No hardcoded fallback - error out if neither yields a path.
			exe ??= SteamPaths.FindExe();
			if (exe == null)
			{
				Console.Error.WriteLine("Could not find AgeOfConan.exe via Steam (app 217750 not installed, or a non-standard Steam path). Pass --exe <path>.");
				return 1;
			}

			if (dx10)
			{
				var dx10Exe = Path.Combine(Path.GetDirectoryName(exe) ?? ".", "AgeOfConanDX10.exe");
				if (File.Exists(dx10Exe))
					exe = dx10Exe;
			}

			if (!File.Exists(exe))
			{
				Console.Error.WriteLine($"Client not found: {exe}\nPass a valid --exe <path>.");
				return 1;
			}

			// --restore: undo every LocalConfig.xml redirect the launcher applies - put the retail Funcom
			// <UniverseAddr> and CDN URLs back - and exit. No backup file is used; the retail values are the
			// constants above.
			if (restore)
			{
				// Copy each <file>.bak back over the modified file: the pristine exe carries the Funcom DH pubkey +
				// GUI subsystem, the pristine LocalConfig.xml the retail UniverseAddr + CDN URLs. Restore BOTH exe
				// variants (DX9 + DX10) regardless of the --dx10 flag, so whichever was patched is undone.
				var dir = Path.GetDirectoryName(exe)!;
				foreach (var name in new[] { "AgeOfConan.exe", "AgeOfConanDX10.exe", "LocalConfig.xml" })
				{
					Console.WriteLine(RestoreFromBackup(Path.Combine(dir, name))
						? $"restored {name} from .bak"
						: $"no {name}.bak to restore");
				}

				Console.WriteLine("restore done.");
				return 0;
			}

			// Back up the pristine exe + LocalConfig.xml to <file>.bak (once - only if no .bak exists yet) before we
			// modify either, so --restore is a straight copy-back of both instead of a reverse-transform.
			if (!dryRun)
			{
				BackupOnce(exe);
				BackupOnce(Path.Combine(Path.GetDirectoryName(exe)!, "LocalConfig.xml"));
			}

			// Patch the client's embedded Funcom DH pubkey -> CELLAO on disk so the server can validate the password
			// (ClientExePatcher). Cross-platform (a byte swap, no ptrace/process memory - the old in-memory patch was
			// Linux-only). Done here, before the md5, so -clienthash matches the launched file; persists like the
			// LocalConfig redirects (undo with --restore). Idempotent - a no-op if already patched.
			if (patchLogin && !dryRun)
			{
				switch (ClientExePatcher.PatchPublicKey(exe, ClientExePatcher.NewPublicKey))
				{
					case ClientExePatcher.Result.Patched:
						Console.WriteLine("login-key : patched DH pubkey (password validation enabled)");
						break;
					case ClientExePatcher.Result.AlreadyDone:
						Console.WriteLine("login-key : DH pubkey already patched");
						break;
					case ClientExePatcher.Result.NotFound:
						Console.Error.WriteLine("login-key : DH pubkey not found in the exe; launching without password validation.");
						break;
				}
			}

			// Redirect the client's hardcoded launcher-notes URL (retail's msgs.ageofconan.com is dead) - same on-disk
			// swap, covered by the exe .bak. Done before the md5 so -clienthash matches the launched file.
			if (launcherNotes != null && !dryRun)
			{
				switch (ClientExePatcher.PatchLauncherNotes(exe, launcherNotes))
				{
					case ClientExePatcher.Result.Patched:
						Console.WriteLine($"notes-url : {launcherNotes}");
						break;
					case ClientExePatcher.Result.AlreadyDone:
						Console.WriteLine("notes-url : launcher-notes URL already redirected");
						break;
					case ClientExePatcher.Result.NotFound:
						Console.Error.WriteLine("notes-url : launcher-notes URL not found in the exe (or replacement too long); skipped.");
						break;
				}
			}

			// -clienthash = md5 of the exe we launch; -key = a fresh launch token.
			var clientHash = Md5Hex(exe);
			var key = ConanLaunchToken.Now();

			var launchArgs = new List<string>();
			if (noConsole)
				launchArgs.Add("-noconsole");
			launchArgs.AddRange(["-clienthash", clientHash, "-key", key]);
			if (username != null)
				launchArgs.AddRange(["-username", username]);
			if (password != null)
				launchArgs.AddRange(["-password", password]);
			if (charname != null)
				launchArgs.AddRange(["-charname", charname]);

			Console.WriteLine($"exe        : {exe}");
			Console.WriteLine($"clienthash : {clientHash}");
			Console.WriteLine($"key        : {key}  (token time = {ConanLaunchToken.GetUnixTime(key)})");
			Console.WriteLine($"args       : {string.Join(' ', launchArgs)}");

			if (dryRun)
				return 0;

			// Kill a client already running from THIS exe path first (so repeated test launches start clean) - a client
			// from a different install is left running.
			KillRunningClients(exe);

			var gameDir = Path.GetDirectoryName(exe)!;

			// Point the client's login server (LocalConfig.xml <UniverseAddr>) at ours. The redirect PERSISTS after
			// this run - it is NOT reverted on exit and no backup/sidecar file is written. Run `--restore` to undo it.
			if (universeAddr != null && SetUniverseAddr(gameDir, universeAddr) != null)
				Console.WriteLine($"redirected UniverseAddr -> {universeAddr}");

			// Point the three LocalConfig.xml CDN keys at the local ContentMirror. Same deal: persists, no revert,
			// no backup file; `--restore` undoes it.
			if (cdnUrl is { } murl && SetCdnConfig(gameDir, murl))
				Console.WriteLine($"redirected CDN keys -> {murl}");

			var psi = new ProcessStartInfo
			{
				WorkingDirectory = gameDir,
				UseShellExecute = false
			};

			// Runner selection (mirrors launchClient.sh): on Windows run the exe directly; with --wine use that
			// command; otherwise launch through Proton (`proton run <exe> <args>`) with the Steam compat env.
			if (OperatingSystem.IsWindows())
				psi.FileName = exe;
			else if (wine != null)
			{
				psi.FileName = wine;
				psi.ArgumentList.Add(exe);
			}
			else
			{
				proton ??= Environment.GetEnvironmentVariable("PROTON") ?? SteamPaths.FindProton();
				if (proton == null)
				{
					Console.Error.WriteLine("Could not find Proton via Steam. Pass --proton <path> (or --wine <cmd>).");
					return 1;
				}

				psi.FileName = proton;
				psi.ArgumentList.Add("run");
				psi.ArgumentList.Add(exe);

				var home = Environment.GetEnvironmentVariable("HOME") ?? "";
				if (SteamPaths.FindCompatData() is { } compat)
					SetIfUnset(psi, "STEAM_COMPAT_DATA_PATH", compat);

				SetIfUnset(psi, "STEAM_COMPAT_CLIENT_INSTALL_PATH", Path.Combine(home, ".steam", "steam"));
				SetIfUnset(psi, "SteamAppId", SteamAppId);
				SetIfUnset(psi, "SteamGameId", SteamAppId);

				SetIfUnset(psi, "PROTON_USE_WINED3D", "1");
				SetIfUnset(psi, "PROTON_NO_ESYNC", "1");
				SetIfUnset(psi, "PROTON_NO_FSYNC", "1");
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

			// With --console, stay in the foreground and tail the log until the client exits. Otherwise launch and return
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

			return proc.HasExited
				? proc.ExitCode
				: 0;
		}

		private static string Md5Hex(string path)
		{
			using var stream = File.OpenRead(path);
			return Convert.ToHexString(MD5.HashData(stream)).ToLowerInvariant();
		}

		private static string NormalizePath(string path)
		{
			return path.Replace('\\', '/').ToLowerInvariant();
		}

		private static void PrintHelp()
		{
			Console.WriteLine("""
			                  Faolan.ClientLauncher - launch AgeOfConan.exe directly (bypassing ConanPatcher).

			                    --exe <path>        path to AgeOfConan.exe (default: auto-detected from your Steam install, app 217750)
			                    --dx10              launch AgeOfConanDX10.exe instead
			                    -u, --username <s>  auto-login account name
			                    -p, --password <s>  auto-login password
			                    -c, --charname <s>  auto-enter this character
			                    --universeaddr <a>  point LocalConfig.xml <UniverseAddr> (the login server) at (host:port)
			                    --cdn <url>			point LocalConfig.xml's three CDN keys (HttpPatchAddr/ClientHttpPatchAddr/
			                                        ControlHttpPatchAddr) at <url> - each becomes <url>/<tag>/upm
			                    --launcher-notes <url>  redirect the client's hardcoded launcher-notes URL to <url> - 
			                                        keep the two %s (UniverseName, Language)
			                    --restore           restore AgeOfConan.exe / AgeOfConanDX10.exe / LocalConfig.xml from their .bak
			                                        (undoes the login/notes exe patch + the CDN/UniverseAddr redirects) and exit
			                    --console           run in the foreground and tail AgeOfConan.log into THIS terminal (same content
			                                        as the client console, no separate wineconsole window); also drops -noconsole
			                    --patch-login       patch the client's DH key ON DISK before launch so the
			                                        server can validate the real password
			                    --proton <path>     (Linux only) Proton 'proton' script or set PROTON (default: auto-detected -
			                                        the build Steam runs AoC with). Ignored on Windows, which runs the exe directly.
			                    --wine <cmd>        (Linux only) use this runner instead of Proton (e.g. 'wine').
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

		// Copy path.bak back over path (if the backup exists). Leaves the .bak in place so it stays the pristine
		// reference for a re-patch. Returns true if a backup was restored.
		private static bool RestoreFromBackup(string path)
		{
			var bak = path + ".bak";
			if (!File.Exists(bak))
				return false;

			File.Copy(bak, path, true);
			return true;
		}

		// Point the three CDN keys at the mirror base URL (LocalConfig.xml is backed up to .bak first, so --restore
		// puts the retail URLs back). Returns true if any key was changed.
		private static bool SetCdnConfig(string gameDir, string baseUrl)
		{
			var b = baseUrl.TrimEnd('/');
			var changed = false;
			// The three LocalConfig.xml CDN keys and the mirror path tag that routes each (the request path carries
			// the tag, so a key -> <base>/<tag>/upm). UniverseAddr (the login server) is separate - see --universeaddr.
			foreach (var (key, tag) in new[] { ("HttpPatchAddr", "update"), ("ClientHttpPatchAddr", "gameupdate"), ("ControlHttpPatchAddr", "control") })
			{
				if (SetConfigTag(gameDir, key, $"{b}/{tag}/upm") != null)
					changed = true;
			}

			return changed;
		}

		// Set a single LocalConfig.xml <tag> to `value`, returning the previous value (so the caller can revert),
		// or null if nothing changed / the file or field is missing. Re-enables a LocalConfig.xml.disabled if that's
		// all that's present (the file is shared by UniverseAddr + the mirror CDN keys).
		private static string? SetConfigTag(string gameDir, string tag, string value)
		{
			try
			{
				var path = Path.Combine(gameDir, "LocalConfig.xml");
				if (!File.Exists(path))
				{
					var disabled = path + ".disabled";
					if (!File.Exists(disabled))
					{
						Console.Error.WriteLine($"{tag}: LocalConfig.xml not found; skipped.");
						return null;
					}

					File.Move(disabled, path);
				}

				var text = File.ReadAllText(path);
				var pattern = $"(<{tag}>)(.*?)(</{tag}>)";
				var match = Regex.Match(text, pattern);
				if (!match.Success)
				{
					Console.Error.WriteLine($"{tag}: no <{tag}> in LocalConfig.xml; skipped.");
					return null;
				}

				var current = match.Groups[2].Value;
				if (current == value)
					return null;

				File.WriteAllText(path, Regex.Replace(text, pattern, m => m.Groups[1].Value + value + m.Groups[3].Value));
				Console.WriteLine($"{tag}: {current} -> {value}");
				return current;
			}
			catch (Exception e)
			{
				Console.Error.WriteLine($"{tag} update failed: {e.Message}");
				return null;
			}
		}

		// Set a child-process env var only if it isn't already set in the real environment (so the shell can override).
		private static void SetIfUnset(ProcessStartInfo psi, string name, string value)
		{
			if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
				psi.Environment[name] = value;
		}

		// Set LocalConfig.xml's <UniverseAddr> (the client's login server) to `addr`, returning the previous value,
		// or null if nothing changed / the file or field is missing.
		private static string? SetUniverseAddr(string gameDir, string addr)
		{
			return SetConfigTag(gameDir, "UniverseAddr", addr);
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
	}
}
