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
		public static string ConfigPath => Environment.GetEnvironmentVariable("FAOLAN_CONFIG") ?? Path.GetFullPath("faolan.json");

		public static readonly string BuildConfiguration;
		public static readonly DateTime BuildDateTimeUtc;
		public static readonly string BuildString;
		public static readonly string StartupBanner;

		// Current UTC unix time as the 32-bit seconds value the wire/DB use (mail/listing timestamps, launch token).

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
