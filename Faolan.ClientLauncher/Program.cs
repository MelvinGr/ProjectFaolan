using Faolan.Core.Crypt;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

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

        // Every LocalConfig.xml key the launcher redirects, with the retail Funcom value `--restore` puts back. The
        // three CDN keys also carry the mirror tag that routes them (so the request path carries the tag); UniverseAddr
        // (the login server) has no tag - it's redirected by --universeaddr, the CDN keys by --mirror. LocalConfig.xml
        // is regenerated locally at startup and is NOT in the file manifest, so a redirect survives a patch run. The
        // launcher applies these redirects and LEAVES them in place (no auto-revert, no backup file); run `--restore`
        // to undo all of them.
        private static readonly (string Key, string Tag, string RetailUrl)[] LocalConfigRedirects =
        [
            ("UniverseAddr", "", "dm09-nj4.ageofconan.com:7010"),
            ("HttpPatchAddr", "update", "http://aoc-eu-update.live.ageofconan.com/upm"),
            ("ClientHttpPatchAddr", "gameupdate", "http://aoc-eu-gameupdate.live.ageofconan.com/upm"),
            ("ControlHttpPatchAddr", "control", "http://aoc-eu-control.live.ageofconan.com/upm")
        ];

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
                foreach (var name in new[]
                {
                    "AgeOfConan",
                    "AgeOfConanDX10"
                })
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
            var exe = DefaultExe;
            string? username = null;
            string? password = null;
            string? charname = null;
            string? wine = null;
            string? universeAddr = null;
            var proton = DefaultProton;
            var dx10 = false;
            var dryRun = false;
            var noConsole = true;
            var streamOutput = false;
            var patchLogin = false;
            var restore = false;
            int? mirrorPort = null;

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

                    // Redirect LocalConfig.xml <UniverseAddr> to our server; the redirect PERSISTS (undo with --restore).
                    // Bare flag defaults to Faolan's 127.0.0.1:7010; replaces the need for an /etc/hosts override.
                    case "--universeaddr":
                    {
                        universeAddr = i + 1 < args.Length && !args[i + 1].StartsWith('-')
                            ? args[++i]
                            : "127.0.0.1:7010";
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

                    // Redirect LocalConfig.xml's three CDN keys to the local ContentMirror so loading-screen content
                    // pulls hit the store-and-forward mirror; PERSISTS (undo with --restore). Bare flag = 7081.
                    case "--mirror":
                    {
                        mirrorPort = i + 1 < args.Length && !args[i + 1].StartsWith('-') && int.TryParse(args[i + 1], out var mp)
                            ? (i++, mp).mp
                            : 7081;
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

            // --restore: undo every LocalConfig.xml redirect the launcher applies - put the retail Funcom
            // <UniverseAddr> and CDN URLs back - and exit. No backup file is used; the retail values are the
            // constants above.
            if (restore)
            {
                var dir = Path.GetDirectoryName(exe)!;
                foreach (var (cfgKey, _, retailUrl) in LocalConfigRedirects)
                {
                    if (SetConfigTag(dir, cfgKey, retailUrl) != null)
                        Console.WriteLine($"restored {cfgKey} -> {retailUrl}");
                }

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
                Console.WriteLine($"redirected UniverseAddr -> {universeAddr} (persists; use --restore to undo)");

            // Point the three LocalConfig.xml CDN keys at the local ContentMirror. Same deal: persists, no revert,
            // no backup file; `--restore` undoes it.
            if (mirrorPort is { } port && SetMirrorConfig(gameDir, port))
                Console.WriteLine($"redirected CDN keys -> 127.0.0.1:{port} (persists; use --restore to undo)");

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

            // Patch the client's embedded DH pubkey (Funcom -> CELLAO) in memory so the server can validate the
            // password (the in-launcher port of tools/patch_aoc_login_key.py). Runs alongside the client; needs
            // ptrace permission (sudo if it can't); harmless if skipped.
            if (patchLogin && !OperatingSystem.IsWindows())
                Task.Run(() => LoginKeyPatcher.PatchWhenReady());

            // With --console, stay in the foreground and tail the log until the client exits. Otherwise launch and
            // return - the client keeps running and the LocalConfig redirects persist (undo with `--restore`).
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

			                    --exe <path>        path to AgeOfConan.exe (or set AOC_EXE; defaults to the Steam path)
			                    --dx10              launch AgeOfConanDX10.exe instead
			                    -u, --username <s>  auto-login account name
			                    -p, --password <s>  auto-login password
			                    -c, --charname <s>  auto-enter this character
			                    --universeaddr [a]  point LocalConfig.xml <UniverseAddr> (the login server) at `a`; bare flag =
			                                        127.0.0.1:7010 (replaces /etc/hosts). PERSISTS - undo with --restore
			                    --mirror [port]     point LocalConfig.xml's three CDN keys (HttpPatchAddr/ClientHttpPatchAddr/
			                                        ControlHttpPatchAddr) at the local Faolan.Web.Content; bare flag = port
			                                        7081. PERSISTS - undo with --restore
			                    --restore           put LocalConfig.xml's UniverseAddr + 3 CDN keys back to the retail Funcom
			                                        URLs and exit (no backup file; the redirects above are not auto-reverted)
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

        // Point the three CDN keys (the tagged LocalConfigRedirects) at the local ContentMirror on `port`. The
        // redirect persists; no backup file is written (`--restore` puts the retail URLs back). Returns true if any
        // key was changed.
        private static bool SetMirrorConfig(string gameDir, int port)
        {
            var changed = false;
            foreach (var (key, tag, _) in LocalConfigRedirects)
            {
                if (tag.Length == 0)
                    continue; // UniverseAddr - not a mirror CDN key

                if (SetConfigTag(gameDir, key, $"http://127.0.0.1:{port}/{tag}/upm") != null)
                    changed = true;
            }

            return changed;
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
