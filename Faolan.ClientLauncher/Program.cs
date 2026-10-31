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

namespace Faolan.ClientLauncher
{
	/// <summary>
	///     A minimal stand-in for ConanPatcher.exe: computes the two flags AgeOfConan.exe needs to run directly,
	///     instead of bouncing back to the patcher, and starts the client.
	/// </summary>
	/// <remarks>
	///     The two flags, as ConanPatcher.exe passes them, are
	///     <c>-clienthash &lt;md5 of AgeOfConan.exe&gt;</c>, the client-integrity hash the patcher forwards, and
	///     <c>-key &lt;TEA-encrypt(time||time)&gt;</c>, the launch-freshness token built by
	///     <see cref="ClientLaunchToken" />. The auto-login flags (-username, -password, -charname) are added on
	///     request, which skips the login and character screens.
	/// </remarks>
	internal static class Program
	{
		private const string Dx10ExeName = "AgeOfConanDX10.exe";
		private const string SteamAppId = "217750";

		/// <summary>
		///     Copies path to path.bak only when the backup does not already exist, so the first run captures the
		///     pristine original and later runs, which see an already-modified file, never overwrite it.
		/// </summary>
		private static void BackupOnce(string path)
		{
			var bak = path + ".bak";
			if (File.Exists(path) && !File.Exists(bak))
				File.Copy(path, bak);
		}

		/// <summary>
		///     Prints any AgeOfConan.log content past <paramref name="pos" /> and returns the new position, restarting
		///     from 0 when the file has shrunk because a new session truncated or recreated it. The file is opened
		///     shared-read so the client can keep writing to and rotating it.
		/// </summary>
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

		/// <summary>
		///     A normalised, case-insensitive fragment of the exe path, used to recognise a running client from the
		///     same install by its command line. The path is compared below its root — leading separator dropped,
		///     backslashes unified to '/', lowercased — so both the native "/mnt/.../AgeOfConan.exe" and the Wine
		///     "Z:\mnt\...\AgeOfConan.exe" forms match the same install while a different install's path does not.
		/// </summary>
		private static string ExePathSignature(string exe)
		{
			var full = exe;
			try
			{
				full = Path.GetFullPath(exe);
			}
			catch
			{
				// Keep the raw value when it cannot be resolved.
			}

			return NormalizePath(full).TrimStart('/');
		}

		/// <summary>
		///     Kills the running AgeOfConan client launched from this <paramref name="exe" /> path only, leaving a
		///     client from a different install alone.
		/// </summary>
		/// <remarks>
		///     Matching is on the exe's full path in the process command line rather than on the "AgeOfConan.exe" name
		///     alone. Under Wine and Proton the path appears in its Z:\ drive form, so the comparison runs on the
		///     normalised signature from <see cref="ExePathSignature" />; on Windows the process main-module path is
		///     compared instead.
		/// </remarks>
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
						// MainModule can throw on access denied, a bitness mismatch or a vanished process.
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
						// Only this install's client: its cmdline must reference the exact exe path, not just the
						// AgeOfConan.exe name, and this launcher is excluded because it carries that path in its
						// own --exe argument.
						var isClient = NormalizePath(cmdline).Contains(sig, StringComparison.Ordinal) && !cmdline.Contains("Faolan.ClientLauncher", StringComparison.Ordinal) && comm != "dotnet";

						if (isClient)
							killed += TryKill(pid);
					}
					catch
					{
						// The process vanished or is unreadable.
					}
				}
			}

			if (killed > 0)
				Console.WriteLine($"killed {killed} running client instance(s)");
		}

		private static int Main(string[] args)
		{
			// The whole command line is declared and parsed in one place, so an unknown or misspelled option is an
			// error here rather than a flag that quietly did nothing.
			if (LauncherCommand.Parse(args, out var parseExit) is not { } options)
				return parseExit;

			// Resolve the client exe: --exe wins, otherwise the Steam install is auto-detected. There is no hardcoded
			// fallback, so neither yielding a path is an error.
			var exe = options.Exe ?? SteamPaths.FindExe();
			if (exe == null)
			{
				Console.Error.WriteLine("Could not find AgeOfConan.exe via Steam (app 217750 not installed, or a non-standard Steam path). Pass --exe <path>.");
				return 1;
			}

			// --dx10 names the sibling exe outright; when it is missing the check below says so, rather than launching the
			// DX9 client the caller did not ask for.
			if (options.Dx10)
				exe = Path.Combine(Path.GetDirectoryName(exe) ?? ".", Dx10ExeName);

			if (!File.Exists(exe))
			{
				Console.Error.WriteLine($"Client not found: {exe}\nPass a valid --exe <path>.");
				return 1;
			}

			// --restore undoes every on-disk change the launcher makes and exits, by copying each <file>.bak back over
			// the modified file: the pristine exe carries the Funcom DH pubkey, the pristine LocalConfig.xml the
			// retail UniverseAddr and CDN URLs, and the pristine dbDebug.conf retail's three logger channels.
			if (options.Restore)
			{
				// Both exe variants are restored regardless of --dx10, so whichever was patched is undone.
				var dir = Path.GetDirectoryName(exe)!;
				var dx10Restored = false;
				foreach (var name in new[] { "AgeOfConan.exe", Dx10ExeName, "LocalConfig.xml", ClientDebugChannels.ConfName })
				{
					var restored = RestoreFromBackup(Path.Combine(dir, name));
					dx10Restored |= restored && name == Dx10ExeName;
					Console.WriteLine(restored
						? $"restored {name} from .bak"
						: $"no {name}.bak to restore");
				}

				// The d3d10 forwarder serves only the redirected DX10 exe, so it goes once that exe imports d3d10.dll again;
				// with no DX10 backup to restore, an exe that still imports it keeps it.
				var forwarder = Path.Combine(dir, D3d10ForwarderDll.FileName);
				if (dx10Restored && File.Exists(forwarder))
				{
					File.Delete(forwarder);
					Console.WriteLine($"removed {D3d10ForwarderDll.FileName}");
				}

				Console.WriteLine("restore done.");
				return 0;
			}

			// Back the pristine exe, LocalConfig.xml and dbDebug.conf up to <file>.bak - once, only when no .bak exists
			// yet - before any of them is modified, so --restore is a straight copy-back and not a reverse transform.
			if (!options.DryRun)
			{
				BackupOnce(exe);
				BackupOnce(Path.Combine(Path.GetDirectoryName(exe)!, "LocalConfig.xml"));
				if (options.DebugChannels.Length > 0)
					BackupOnce(Path.Combine(Path.GetDirectoryName(exe)!, ClientDebugChannels.ConfName));
			}

			// Patch the client's embedded Funcom DH pubkey on disk so the server can validate the password. It is a
			// plain byte swap in the file, with no process memory access, so it works on every platform. It runs here,
			// before the md5, so -clienthash matches the launched file; it persists like the LocalConfig redirects and
			// is undone by --restore, and it is idempotent.
			if (options is { PatchLogin: true, DryRun: false })
			{
				// The DH keys come from the same configuration the server reads (faolan.json, then these flags, then
				// the environment), so a deployment that overrides Server:PrivateKey patches the matching public key
				// with no second setting to keep in step. A malformed key throws here rather than producing an exe
				// that silently cannot log in.
				//
				// Which key goes in is decided at this one call site rather than inside the crypto. Normally it is the
				// public half derived from Server:PrivateKey, because the client must carry our public key for the
				// server to reproduce the shared secret; an explicit Server:PublicKey wins for the one case where the
				// two genuinely differ, which is patching a client to talk to somebody else's server, whose private
				// key the operator does not have and must not need.
				string? publicHex = null;
				if (options.Configuration.ServerPublicKey is { } explicitPublic)
				{
					publicHex = explicitPublic.ToHex(ClientLoginEncryption.PublicHexLength);
					Console.WriteLine("login-key : using the configured Server:PublicKey - this client will NOT log in to a server holding a different private key");
				}
				else if (options.Configuration.ServerPrivateKey is { } serverPrivate)
				{
					publicHex = ClientLoginEncryption.PublicHexFor(serverPrivate);
					Console.WriteLine("login-key : deriving the public key from the configured Server:PrivateKey");
				}

				// With no key on either side there is nothing to patch in. Skipping is the honest outcome - the client
				// keeps Funcom's key, which is exactly what a keyless server expects, since it checks no passwords -
				// but it is said out loud, because --patch-login was asked for and did not happen.
				if (publicHex == null)
				{
					Console.Error.WriteLine("login-key : no Server:PrivateKey is set, so there is no public key to patch in - launching unpatched. The server accepts this client on its account name alone; run `dotnet run --project Faolan -- --generate-login-key` to enable password validation.");
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

			// Redirect the client's hardcoded launcher-notes URL, retail's now-dead msgs.ageofconan.com. It is the same
			// on-disk swap, covered by the exe .bak, and runs before the md5 so -clienthash matches the launched file.
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

			// Under Wine, point the DX10 client's d3d10.dll import at the forwarder written next to it, which routes the
			// D3D10PreprocessShader Wine leaves unimplemented to d3dcompiler_43 (D3d10ForwarderDll says why). It is an exe
			// patch like the two above: covered by the .bak, run before the md5, and NotFound on the DX9 exe, which imports
			// no d3d10.dll. Windows's own d3d10.dll implements the function, so the exe is left alone there.
			if (!OperatingSystem.IsWindows() && !options.DryRun)
			{
				var redirect = ClientExePatcher.RedirectD3d10Import(exe, out var d3d10Exports);
				if (redirect != ClientExePatcher.Result.NotFound)
				{
					File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(exe)!, D3d10ForwarderDll.FileName), D3d10ForwarderDll.Build(d3d10Exports));
					Console.WriteLine(redirect == ClientExePatcher.Result.Patched
						? $"d3d10     : import redirected to {D3d10ForwarderDll.FileName} ({D3d10ForwarderDll.StubbedExport} -> d3dcompiler_43)"
						: $"d3d10     : import already redirected; {D3d10ForwarderDll.FileName} rewritten");
				}
			}

			// -clienthash is the md5 of the exe being launched; -key is a fresh launch token.
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

			// Kill a client already running from this exe path first, so repeated test launches start clean; a client
			// from a different install is left running.
			KillRunningClients(exe);

			var gameDir = Path.GetDirectoryName(exe)!;

			// Point the client's login server (LocalConfig.xml <UniverseAddr>) at ours, using the advertised address and
			// the UniverseAgent port the server itself is configured with, so the client dials what the server binds.
			// The redirect persists after this run rather than being reverted on exit; --restore undoes it from the
			// .bak taken above.
			if (options.PatchUniverseAddr)
			{
				var universeAddr = options.Configuration.ServerUniverseAddress;
				if (SetUniverseAddr(gameDir, universeAddr) != null)
					Console.WriteLine($"redirected UniverseAddr -> {universeAddr}");
			}

			// Point the three LocalConfig.xml CDN keys at the local ContentMirror, at the address the mirror is
			// configured to be reachable on: Cdn:PublicAddress, not Cdn:ListenAddress, which names an interface. This
			// redirect persists too, and --restore undoes it.
			if (options.PatchCdn)
			{
				// The only way this can be blank is an explicitly emptied key. It is reported rather than silently
				// launching a client still pointed at Funcom's dead CDN, which would read as a --patch-cdn that
				// worked.
				if (options.Configuration.CdnPublicAddress is not { Length: > 0 } murl)
				{
					Console.Error.WriteLine(
						"--patch-cdn : Cdn:PublicAddress is not set - there is no mirror address to redirect to. Set it in faolan.json / faolan.user.json, " +
						"in the environment as Cdn__PublicAddress, or on the command line as --Cdn:PublicAddress.");
				}
				else if (SetCdnConfig(gameDir, murl))
					Console.WriteLine($"redirected CDN keys -> {murl}");
			}

			// Open the client's own debug channels by writing its logger config, dbDebug.conf. This must happen before
			// the launch: the client reads that file once during startup, so the channels are live from its first
			// line, and writing a file needs no process memory access, which is what makes it work on Windows too. It
			// persists like the other redirects, and --restore puts retail's file back.
			if (options.DebugChannels.Length > 0)
				ClientDebugChannels.Configure(gameDir, options.DebugChannels, Console.WriteLine);

			var psi = new ProcessStartInfo
			{
				WorkingDirectory = gameDir,
				UseShellExecute = false
			};

			// Runner selection: on Windows run the exe directly; with --wine use that command; otherwise launch through
			// Proton as `proton run <exe> <args>` with the Steam compat environment.
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

				// The DX9 client needs WineD3D: on Proton's default DXVK its d3d9 device dies at startup inside NVIDIA's
				// 32-bit driver (an access violation in libnvidia-glcore 615.78.08 under vkAllocateMemory). The DX10 client
				// runs on DXVK, so it keeps the default. Everything else, esync/fsync included, is Proton's default, and
				// PROTON_USE_WINED3D, PROTON_NO_ESYNC and the like set in the environment pass through.
				if (!Path.GetFileName(exe).Equals(Dx10ExeName, StringComparison.OrdinalIgnoreCase))
					SetIfUnset(psi, "PROTON_USE_WINED3D", "1");
			}

			foreach (var a in launchArgs)
				psi.ArgumentList.Add(a);

			// The client writes its console output through WriteConsoleW to a Wine console, which is lost here, but the
			// same content also goes to AgeOfConan.log, so --console tails that log into this terminal while proton's
			// own messages keep inheriting our stdio. The log's pre-launch size is recorded so only this session is
			// shown, since the client truncates or rotates the file on start.
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

			// With --console, stay in the foreground and tail the log until the client exits; otherwise launch and
			// return immediately.
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

		/// <summary>
		///     Copies path.bak back over path when the backup exists, leaving the .bak in place so it stays the
		///     pristine reference for a re-patch.
		/// </summary>
		/// <returns>True when a backup was restored.</returns>
		private static bool RestoreFromBackup(string path)
		{
			var bak = path + ".bak";
			if (!File.Exists(bak))
				return false;

			File.Copy(bak, path, true);
			return true;
		}

		/// <summary>
		///     Points the three CDN keys at the mirror base URL. LocalConfig.xml is backed up to .bak first, so
		///     --restore puts the retail URLs back.
		/// </summary>
		/// <returns>True when any key was changed.</returns>
		private static bool SetCdnConfig(string gameDir, string baseUrl)
		{
			var b = baseUrl.TrimEnd('/');
			var changed = false;
			// The three LocalConfig.xml CDN keys and the mirror path tag that routes each: the request path carries the
			// tag, so a key becomes <base>/<tag>/upm. UniverseAddr, the login server, is separate and is handled by
			// --patch-universeaddr.
			foreach (var (key, tag) in new[] { ("HttpPatchAddr", "update"), ("ClientHttpPatchAddr", "gameupdate"), ("ControlHttpPatchAddr", "control") })
			{
				if (SetConfigTag(gameDir, key, $"{b}/{tag}/upm") != null)
					changed = true;
			}

			return changed;
		}

		/// <summary>
		///     Sets a single LocalConfig.xml tag to <paramref name="value" />. A LocalConfig.xml.disabled is re-enabled
		///     when that is all that is present, since the file is shared by UniverseAddr and the mirror CDN keys.
		/// </summary>
		/// <returns>
		///     The previous value, so the caller can revert, or null when nothing changed or the file or field is
		///     missing.
		/// </returns>
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

		/// <summary>
		///     Sets a child-process environment variable only when it is not already set in the real environment, so
		///     the shell can override it.
		/// </summary>
		private static void SetIfUnset(ProcessStartInfo psi, string name, string value)
		{
			if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
				psi.Environment[name] = value;
		}

		/// <summary>
		///     Sets LocalConfig.xml's UniverseAddr, the client's login server, to <paramref name="addr" />.
		/// </summary>
		/// <returns>The previous value, or null when nothing changed or the file or field is missing.</returns>
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
