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

		/// <summary>
		///     The shared config file, for every executable that reads it (server, importer, web, CDN mirror, MCP
		///     bridge). It sits next to the exe - or at the solution root when debugging, where the working directory
		///     is the same place - so the plain relative name resolves in both cases. <c>FAOLAN_CONFIG</c> overrides it
		///     for a container or a one-off run.
		/// </summary>
		public static string ConfigPath => Environment.GetEnvironmentVariable("FAOLAN_CONFIG") ?? FindUpwards("faolan.json");

		/// <summary>
		///     The developer's own overrides, found the same way and layered on top of faolan.json. Optional; absent is
		///     the normal case.
		/// </summary>
		/// <remarks>
		///     Gitignored, and that is the point: faolan.json is committed deployment config that must stay
		///     machine-agnostic, so anything true only of one box - an RDB folder, a CDN mirror on another machine, a
		///     scratch database - goes here instead of being edited into the committed file.
		/// </remarks>
		public static string UserConfigPath => Environment.GetEnvironmentVariable("FAOLAN_USER_CONFIG") ?? FindUpwards("faolan.user.json");

		/// <summary>
		///     The directory <see cref="ConfigPath" /> resolves to.
		/// </summary>
		/// <remarks>
		///     A relative path inside the config - the SQLite <c>Data Source</c> above all - means "beside
		///     faolan.json", not "in whatever directory this process happened to be started from". Resolving it
		///     against the working directory instead makes a tool run from a project subdirectory quietly create an
		///     empty database there.
		/// </remarks>
		public static string ConfigDirectory => Path.GetDirectoryName(ConfigPath) is { Length: > 0 } directory
			? directory
			: Directory.GetCurrentDirectory();

		/// <summary>
		///     The game-data seed, found the same way as the config: beside the exe, in the working directory, up the
		///     tree, or wherever <c>FAOLAN_SEED</c> points. <c>DatabaseInitializer</c> loads it into a database that
		///     comes up with no content, which is how a clone gets its game data: the repository ships no faolan.db.
		/// </summary>
		public static string SeedPath => Environment.GetEnvironmentVariable("FAOLAN_SEED") ?? FindUpwards("faolan.sql");

		/// <summary>
		///     The first <paramref name="name" /> at or above the working directory, else at or above the exe's own
		///     directory, else the working-directory-relative path, so a genuinely missing file still reports the
		///     place you would look first.
		/// </summary>
		/// <remarks>
		///     Walking up is what lets <c>dotnet run --project Faolan.Web</c> work: it sets the working directory to
		///     the project directory, and the config lives at the repo root above it. Every executable in the solution
		///     is run either from the root or from a directory beneath it, so the first hit going up is always the
		///     repo's own file. An explicit <c>FAOLAN_CONFIG</c> still wins over the search.
		/// </remarks>
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
			// The build metadata rides on the entry assembly (Faolan.dll). It is absent under a test host or when
			// loaded as a library, so degrade gracefully rather than throwing - accessing any Global member must never
			// depend on the build info being present.
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
