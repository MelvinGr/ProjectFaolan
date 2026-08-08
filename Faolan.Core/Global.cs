using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Faolan.Core.Extensions;

namespace Faolan.Core
{
	public static class Global
	{
#if DEBUG
		public static bool IsDebug => true;
#else
        public static bool IsDebug => false;
#endif

		// The shared config file, for every exe that reads it (server, importer, web, CDN mirror, MCP bridge). It sits
		// next to the exe - or at the solution root when debugging, where the CWD is set to the same place - so the
		// plain relative name resolves in both cases; FAOLAN_CONFIG overrides it (container / one-off run).
		public static string ConfigPath => Environment.GetEnvironmentVariable("FAOLAN_CONFIG") ?? FindUpwards("faolan.json");

		// The developer's own overrides, found the same way and layered ON TOP of faolan.json. GITIGNORED, and that is
		// the point: faolan.json is committed deployment config that must stay machine-agnostic (CLAUDE.md's
		// no-local-paths rule), so anything true only of this box - an RDB folder, a CDN mirror on another machine,
		// a scratch database - goes here instead of being edited into the committed file and accidentally committed.
		// Optional; absent is the normal case.
		public static string UserConfigPath => Environment.GetEnvironmentVariable("FAOLAN_USER_CONFIG") ?? FindUpwards("faolan.user.json");

		// The directory ConfigPath resolves to. A RELATIVE path inside the config - the SQLite `Data Source` above
		// all - means "beside faolan.json", not "in whatever directory this process happened to be started from".
		// Reading it against the CWD is the trap CLAUDE.md documents for `dotnet ef database update` and the one that
		// made every DB-backed Faolan.Web page 500 (#456): the app resolved `Data Source=faolan.db` against
		// Faolan.Web/ and quietly created an empty database there.
		public static string ConfigDirectory => Path.GetDirectoryName(ConfigPath) is { Length: > 0 } directory
			? directory
			: Directory.GetCurrentDirectory();

		// The game-data seed (#412), found the same way as the config: beside the exe / in the CWD / up the tree, or
		// wherever FAOLAN_SEED points. DatabaseInitializer loads it into a database that comes up with no content,
		// which is what lets a clone run without the committed faolan.db.
		public static string SeedPath => Environment.GetEnvironmentVariable("FAOLAN_SEED") ?? FindUpwards("faolan.sql");

		// The first `name` at or above the CWD, else at or above the exe's own directory, else the CWD-relative path
		// (today's behaviour, so a genuinely missing file still reports the place you would look first).
		//
		// Walking UP is what lets `dotnet run --project Faolan.Web` work: it sets the CWD to the PROJECT directory,
		// and the config lives at the repo root above it. Every exe in the solution is run either from the root or
		// from a directory beneath it, so the first hit going up is always the repo's own file - there is no
		// ambiguity to resolve, and an explicit FAOLAN_CONFIG still wins over the search.
		private static string FindUpwards(string name)
		{
			foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
			{
				for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
				{
					var candidate = Path.Combine(directory.FullName, name);
					if (File.Exists(candidate))
						return candidate;
				}
			}

			return Path.GetFullPath(name);
		}

		public static readonly string BuildConfiguration;
		public static readonly DateTime BuildDateTimeUtc;
		public static readonly string BuildString;
		public static readonly string StartupBanner;

		public static readonly bool EnableTutorialOverlay = false;

		static Global()
		{
			// The build metadata rides on the ENTRY assembly (Faolan.dll). It is absent under a test host or when
			// loaded as a library, so degrade gracefully (don't throw) - accessing any Global member, e.g.
			// NowUnixTimeSeconds, must never depend on the build info being present.
			var entryAssembly = Assembly.GetEntryAssembly();

			var assemblyMetadata = entryAssembly?.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(k => k.Key, v => v.Value ?? "") ?? [];

			BuildConfiguration = assemblyMetadata.GetValueOrDefault("BuildConfiguration", "unknown");
			BuildDateTimeUtc = assemblyMetadata.TryGetValue("BuildDateTimeUtc", out var buildDate)
				? DateTime.Parse(buildDate, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
				: default;

			// The SDK appends "+<commit-sha>" to the informational version (IncludeSourceRevisionInInformationalVersion),
			// so the HEAD SHA rides along for free - no custom metadata needed.
			var informationalVersion = entryAssembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
			var plusIndex = informationalVersion.IndexOf('+');
			var gitSha = plusIndex >= 0
				? informationalVersion[(plusIndex + 1)..]
				: informationalVersion;

			BuildString = $"{BuildConfiguration} {gitSha} @ {BuildDateTimeUtc:G} UTC";

			StartupBanner = "-------------------------------------------------------------------------------\n" + //
							"|  ####  ####   ###    # #### ### #####    ####   #    ###  #      #   #   #  |\n" + //
							"|  #   # #   # #   #   # #    #     #      #     # #  #   # #     # #  ##  #  |\n" + //
							"|  ####  ####  #   #   # #### #     #      #### #   # #   # #    #   # # # #  |\n" + //
							"|  #     #  #  #   #   # #    #     #      #    ##### #   # #    ##### #  ##  |\n" + //
							"|  #     #   #  ###  ### #### ###   #      #    #   #  ###  #### #   # #   #  |\n" + //
							"-------------------------------------------------------------------------------\n" + //
							"| " + BuildString.PadBoth(75) + " |\n" + //
							"-------------------------------------------------------------------------------\n";
		}
	}
}
