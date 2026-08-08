using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Faolan.Core.Cli;
using Faolan.Core.Crypt;
using Faolan.Core.Extensions;
using Microsoft.Extensions.Configuration;

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
			// The whole command line, declared and parsed in one place (LauncherCommand) - an unknown or misspelled
			// option is an error here, not a flag that quietly did nothing.
			if (LauncherCommand.Parse(args, out var parseExit) is not { } options)
				return parseExit;

			// Resolve the client exe: --exe wins, else auto-detect the Steam install (app 217750, Linux + Windows).
			// No hardcoded fallback - error out if neither yields a path.
			var exe = options.Exe ?? SteamPaths.FindExe();
			if (exe == null)
			{
				Console.Error.WriteLine("Could not find AgeOfConan.exe via Steam (app 217750 not installed, or a non-standard Steam path). Pass --exe <path>.");
				return 1;
			}

			if (options.Dx10)
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
			if (options.Restore)
			{
				// Copy each <file>.bak back over the modified file: the pristine exe carries the Funcom DH pubkey +
				// GUI subsystem, the pristine LocalConfig.xml the retail UniverseAddr + CDN URLs, the pristine
				// dbDebug.conf retail's three logger channels. Restore BOTH exe
				// variants (DX9 + DX10) regardless of the --dx10 flag, so whichever was patched is undone.
				var dir = Path.GetDirectoryName(exe)!;
				foreach (var name in new[] { "AgeOfConan.exe", "AgeOfConanDX10.exe", "LocalConfig.xml", ClientDebugChannels.ConfName })
				{
					Console.WriteLine(RestoreFromBackup(Path.Combine(dir, name))
						? $"restored {name} from .bak"
						: $"no {name}.bak to restore");
				}

				Console.WriteLine("restore done.");
				return 0;
			}

			// Back up the pristine exe + LocalConfig.xml + dbDebug.conf to <file>.bak (once - only if no .bak exists
			// yet) before we modify any of them, so --restore is a straight copy-back instead of a reverse-transform.
			if (!options.DryRun)
			{
				BackupOnce(exe);
				BackupOnce(Path.Combine(Path.GetDirectoryName(exe)!, "LocalConfig.xml"));
				if (options.DebugChannels.Length > 0)
					BackupOnce(Path.Combine(Path.GetDirectoryName(exe)!, ClientDebugChannels.ConfName));
			}

			// Patch the client's embedded Funcom DH pubkey -> CELLAO on disk so the server can validate the password
			// (ClientExePatcher). Cross-platform (a byte swap, no ptrace/process memory - the old in-memory patch was
			// Linux-only). Done here, before the md5, so -clienthash matches the launched file; persists like the
			// LocalConfig redirects (undo with --restore). Idempotent - a no-op if already patched.
			if (options is { PatchLogin: true, DryRun: false })
			{
				// Load the DH keys from the SAME config the server reads (faolan.json < these flags < environment), so a
				// deployment that overrides Login:PrivateKey patches the matching public key without a second setting to
				// keep in step. A malformed key throws here rather than producing an exe that silently cannot log in.
				//
				// WHICH key goes in is decided here, at the one call site that patches, rather than inside the crypto:
				// normally the public half DERIVED from Login:PrivateKey - the client must carry our public key for the
				// server to reproduce the shared secret - but an explicit Login:PublicKey wins, for the one case where
				// the two genuinely differ: patching a client to talk to somebody ELSE's server, whose private key you
				// do not have and must not need. Which is also the one way there is a key to patch without a private one.
				string? publicHex = null;
				if (options.Configuration.LoginPublicKey is { } explicitPublic)
				{
					publicHex = explicitPublic.ToHex(ClientLoginEncryption.PublicHexLength);
					Console.WriteLine("login-key : using the configured Login:PublicKey - this client will NOT log in to a server holding a different private key");
				}
				else if (options.Configuration.LoginPrivateKey is { } serverPrivate)
				{
					publicHex = ClientLoginEncryption.PublicHexFor(serverPrivate);
					Console.WriteLine("login-key : deriving the public key from the configured Login:PrivateKey");
				}

				// No key on either side leaves nothing to patch IN. Skipping is the honest outcome - the client keeps
				// Funcom's key, which is exactly what a keyless server expects (it checks no passwords) - but it is
				// said out loud, because --patch-login was asked for and did not happen.
				if (publicHex == null)
				{
					Console.Error.WriteLine("login-key : no Login:PrivateKey is set, so there is no public key to patch in - launching unpatched. The server accepts this client on its account name alone; run `dotnet run --project Faolan -- --generate-login-key` to enable password validation.");
				}
				else
				{
					switch (ClientExePatcher.PatchPublicKey(exe, Encoding.ASCII.GetBytes(publicHex)))
					{
						case ClientExePatcher.Result.Patched:
						{
							Console.WriteLine("login-key : patched DH pubkey (password validation enabled)");
							break;
						}

						case ClientExePatcher.Result.AlreadyDone:
						{
							Console.WriteLine("login-key : DH pubkey already patched");
							break;
						}

						case ClientExePatcher.Result.NotFound:
						{
							Console.Error.WriteLine("login-key : DH pubkey not found in the exe; launching without password validation.");
							break;
						}
					}
				}
			}

			// Redirect the client's hardcoded launcher-notes URL (retail's msgs.ageofconan.com is dead) - same on-disk
			// swap, covered by the exe .bak. Done before the md5 so -clienthash matches the launched file.
			if (options is { LauncherNotes: { } launcherNotes, DryRun: false })
			{
				switch (ClientExePatcher.PatchLauncherNotes(exe, launcherNotes))
				{
					case ClientExePatcher.Result.Patched:
					{
						Console.WriteLine($"notes-url : {launcherNotes}");
						break;
					}

					case ClientExePatcher.Result.AlreadyDone:
					{
						Console.WriteLine("notes-url : launcher-notes URL already redirected");
						break;
					}

					case ClientExePatcher.Result.NotFound:
					{
						Console.Error.WriteLine("notes-url : launcher-notes URL not found in the exe (or replacement too long); skipped.");
						break;
					}
				}
			}

			// -clienthash = md5 of the exe we launch; -key = a fresh launch token.
			var clientHash = Md5Hex(exe);
			var key = ClientLaunchToken.Now();

			var launchArgs = new List<string>();
			if (!options.Console)
				launchArgs.Add("-noconsole");
			launchArgs.AddRange(["-clienthash", clientHash, "-key", key]);
			if (options.Username != null)
				launchArgs.AddRange(["-username", options.Username]);
			if (options.Password != null)
				launchArgs.AddRange(["-password", options.Password]);
			if (options.Charname != null)
				launchArgs.AddRange(["-charname", options.Charname]);

			Console.WriteLine($"exe        : {exe}");
			Console.WriteLine($"clienthash : {clientHash}");
			Console.WriteLine($"key        : {key}  (token time = {ClientLaunchToken.GetUnixTime(key)})");
			Console.WriteLine($"args       : {string.Join(' ', launchArgs)}");

			if (options.DryRun)
				return 0;

			// Kill a client already running from THIS exe path first (so repeated test launches start clean) - a client
			// from a different install is left running.
			KillRunningClients(exe);

			var gameDir = Path.GetDirectoryName(exe)!;

			// Point the client's login server (LocalConfig.xml <UniverseAddr>) at ours - the ADVERTISED address and the
			// UniverseAgent port the server itself is configured with, so the client dials what the server binds. The
			// redirect PERSISTS after this run - it is NOT reverted on exit and no backup/sidecar file is written. Run
			// `--restore` to undo it.
			if (options.PatchUniverseAddr)
			{
				var universeAddr = options.Configuration.UniverseAddress;
				if (SetUniverseAddr(gameDir, universeAddr) != null)
					Console.WriteLine($"redirected UniverseAddr -> {universeAddr}");
			}

			// Point the three LocalConfig.xml CDN keys at the local ContentMirror, at the address the mirror is
			// configured to be reachable ON (Cdn:PublicAddress - NOT Cdn:ListenAddress, which is an interface). Same
			// deal: persists, no revert, no backup file; `--restore` undoes it.
			if (options.PatchCdn)
			{
				// The one way this can be blank is an explicitly emptied key. Say so rather than silently launching a
				// client still pointed at Funcom's dead CDN, which is a --patch-cdn that reads as if it worked.
				if (options.Configuration.CdnPublicAddress is not { Length: > 0 } murl)
				{
					Console.Error.WriteLine(
						"--patch-cdn : Cdn:PublicAddress is not set - there is no mirror address to redirect to. Set it in faolan.json / faolan.user.json, " +
						"in the environment as Cdn__PublicAddress, or on the command line as --Cdn:PublicAddress.");
				}
				else if (SetCdnConfig(gameDir, murl))
					Console.WriteLine($"redirected CDN keys -> {murl}");
			}

			// Open the client's own debug channels by writing its logger config (dbDebug.conf). BEFORE the launch: the
			// client reads that file once, during startup, so the channels are live from its first line - and it needs
			// no process memory access, which is what makes it work on Windows too. Persists like the other redirects;
			// `--restore` puts retail's file back.
			if (options.DebugChannels.Length > 0)
				ClientDebugChannels.Configure(gameDir, options.DebugChannels, Console.WriteLine);

			var psi = new ProcessStartInfo
			{
				WorkingDirectory = gameDir,
				UseShellExecute = false
			};

			// Runner selection (mirrors launchClient.sh): on Windows run the exe directly; with --wine use that
			// command; otherwise launch through Proton (`proton run <exe> <args>`) with the Steam compat env.
			if (OperatingSystem.IsWindows())
				psi.FileName = exe;
			else if (options.Wine is { } wine)
			{
				psi.FileName = wine;
				psi.ArgumentList.Add(exe);
			}
			else
			{
				var proton = options.Proton ?? Environment.GetEnvironmentVariable("PROTON") ?? SteamPaths.FindProton();
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
			var logStart = options.Console && File.Exists(logPath)
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
			if (options.Console)
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
			// the tag, so a key -> <base>/<tag>/upm). UniverseAddr (the login server) is separate - see --patch-universeaddr.
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
