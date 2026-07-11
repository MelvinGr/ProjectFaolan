using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Faolan.ClientLauncher
{
	// Auto-discovers the Age of Conan install from Steam, so the launcher needs no hardcoded machine-specific paths.
	// Steam records every library in `libraryfolders.vdf`; AoC is app 217750. From the library holding it we derive
	// the exe, the Proton compat-data prefix, and (via that prefix's config_info) the exact Proton build Steam runs it
	// with. Works on Linux (~/.steam/...) and Windows (default Steam install dirs). Returns null when not found -
	// callers error out (there is no fallback).
	internal static class SteamPaths
	{
		private const string AocAppId = "217750";

		// The Proton compat-data prefix for AoC (STEAM_COMPAT_DATA_PATH), or null.
		public static string? FindCompatData()
		{
			if (AocLibrary() is not { } lib)
				return null;

			var compat = Path.Combine(lib, "steamapps", "compatdata", AocAppId);
			return Directory.Exists(compat)
				? compat
				: null;
		}

		// The AgeOfConan.exe path (the DX10 sibling is derived by the caller from its directory), or null.
		public static string? FindExe()
		{
			if (AocLibrary() is not { } lib)
				return null;

			var exe = Path.Combine(lib, "steamapps", "common", "Age of Conan", "AgeOfConan.exe");
			return File.Exists(exe)
				? exe
				: null;
		}

		// The Proton `proton` runner script: prefer the EXACT build the compat-data's config_info records (the one
		// Steam last ran AoC with), else the newest Proton found across libraries. Null if none.
		public static string? FindProton()
		{
			if (FindCompatData() is { } compat && File.Exists(Path.Combine(compat, "config_info")))
			{
				// config_info line 2/3 point inside the Proton install: ".../Proton - Experimental/files/share/...".
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

		// The library that has AoC installed (its steamapps carries the appmanifest).
		private static string? AocLibrary()
		{
			return Libraries().FirstOrDefault(l => File.Exists(Path.Combine(l, "steamapps", $"appmanifest_{AocAppId}.acf")));
		}

		// Every Steam library path (the first existing root itself + the paths in its libraryfolders.vdf).
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
					libs.Add(m.Groups[1].Value.Replace("\\\\", "\\")); // vdf doubles backslashes on Windows
			}

			return libs.Distinct().ToList();
		}

		// The Steam roots to probe, in order. First existing one wins.
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
