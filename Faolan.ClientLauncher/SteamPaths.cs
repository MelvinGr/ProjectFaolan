using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Faolan.ClientLauncher
{
	/// <summary>
	///     Auto-discovers the Age of Conan install from Steam, so the launcher needs no machine-specific paths.
	/// </summary>
	/// <remarks>
	///     Steam records every library in <c>libraryfolders.vdf</c> and AoC is app 217750. The library holding it gives
	///     the exe, the Proton compat-data prefix and, through that prefix's <c>config_info</c>, the exact Proton build
	///     Steam runs it with. Linux (under ~/.steam) and Windows (the default Steam install directories) are both
	///     probed. Every lookup returns null when nothing is found: callers error out, as there is no fallback.
	/// </remarks>
	internal static class SteamPaths
	{
		private const string AocAppId = "217750";

		/// <summary>
		///     The Proton compat-data prefix for AoC, the value of STEAM_COMPAT_DATA_PATH, or null.
		/// </summary>
		public static string? FindCompatData()
		{
			if (AocLibrary() is not { } lib)
				return null;

			var compat = Path.Combine(lib, "steamapps", "compatdata", AocAppId);
			return Directory.Exists(compat)
				? compat
				: null;
		}

		/// <summary>
		///     The AgeOfConan.exe path, or null. The caller derives the DX10 sibling from its directory.
		/// </summary>
		public static string? FindExe()
		{
			if (AocLibrary() is not { } lib)
				return null;

			var exe = Path.Combine(lib, "steamapps", "common", "Age of Conan", "AgeOfConan.exe");
			return File.Exists(exe)
				? exe
				: null;
		}

		/// <summary>
		///     The Proton runner script: the exact build the compat-data's <c>config_info</c> records, which is the one
		///     Steam last ran AoC with, or otherwise the newest Proton found across the libraries. Null if there is
		///     none.
		/// </summary>
		public static string? FindProton()
		{
			if (FindCompatData() is { } compat && File.Exists(Path.Combine(compat, "config_info")))
			{
				// config_info lines 2 and 3 point inside the Proton install, as ".../Proton - Experimental/files/...".
				foreach (var line in File.ReadLines(Path.Combine(compat, "config_info")))
				{
					var i = line.IndexOf("/files/", StringComparison.Ordinal);
					if (i <= 0)
						continue;

					var proton = Path.Combine(line[..i], "proton");
					if (File.Exists(proton))
						return proton;
				}
			}

			foreach (var lib in Libraries())
			{
				var exp = Path.Combine(lib, "steamapps", "common", "Proton - Experimental", "proton");
				if (File.Exists(exp))
					return exp;
			}

			foreach (var lib in Libraries())
			{
				var common = Path.Combine(lib, "steamapps", "common");
				if (!Directory.Exists(common))
					continue;

				var newest = Directory.GetDirectories(common, "Proton*").OrderByDescending(d => d, StringComparer.Ordinal).Select(d => Path.Combine(d, "proton")).FirstOrDefault(File.Exists);
				if (newest != null)
					return newest;
			}

			return null;
		}

		/// <summary>
		///     The library that has AoC installed, identified by the appmanifest in its steamapps directory.
		/// </summary>
		private static string? AocLibrary()
		{
			return Libraries().FirstOrDefault(l => File.Exists(Path.Combine(l, "steamapps", $"appmanifest_{AocAppId}.acf")));
		}

		/// <summary>
		///     Every Steam library path: the first existing root itself plus the paths in its libraryfolders.vdf.
		/// </summary>
		private static List<string> Libraries()
		{
			var root = SteamRoots().FirstOrDefault(Directory.Exists);
			var libs = new List<string>();
			if (root == null)
				return libs;

			libs.Add(root);
			var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
			if (File.Exists(vdf))
			{
				foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"([^\"]+)\""))
					libs.Add(m.Groups[1].Value.Replace("\\\\", "\\")); // A vdf doubles backslashes on Windows.
			}

			return libs.Distinct().ToList();
		}

		/// <summary>
		///     The Steam roots to probe, in order; the first existing one wins.
		/// </summary>
		private static IEnumerable<string> SteamRoots()
		{
			if (OperatingSystem.IsWindows())
			{
				foreach (var pf in new[] { "ProgramFiles(x86)", "ProgramFiles" })
				{
					if (Environment.GetEnvironmentVariable(pf) is { Length: > 0 } dir)
						yield return Path.Combine(dir, "Steam");
				}
			}
			else
			{
				var home = Environment.GetEnvironmentVariable("HOME") ?? "";
				yield return Path.Combine(home, ".steam", "steam");
				yield return Path.Combine(home, ".steam", "root");
				yield return Path.Combine(home, ".local", "share", "Steam");
				yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
			}
		}
	}
}
