using System;
using System.Collections.Generic;
using System.Globalization;
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

		// Account "premium" (subscribed) flag. Decoded by diffing the faolan-account-login-{nopremium,premium}
		// captures: the only per-account difference in the PlayerAgent character list (0x20EF) trailing block is
		// the subscribed flag byte 0x81 -> 0x80 and the unlocked-character-slot count 4 -> 10. Enabled globally so
		// every account is treated as subscribed (see SendCharacterList).
		public static bool Premium = true;

		// Unlocked character-slot cap advertised in the 0x20EF trailing block (premium when subscribed, else free).
		public const uint PremiumCharSlots = 10;
		public const uint FreeCharSlots = 4;

		public static readonly string BuildConfiguration;
		public static readonly DateTime BuildDateTimeUtc;
		public static readonly string BuildString;
		public static readonly string StartupBanner;

		// Current UTC unix time as the 32-bit seconds value the wire/DB use (mail/listing timestamps, launch token).
		public static uint NowUnixTimeSeconds => (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

		static Global()
		{
			// The build metadata rides on the ENTRY assembly (Faolan.dll). It is absent under a test host or when
			// loaded as a library, so degrade gracefully (don't throw) - accessing any Global member, e.g.
			// NowUnixTimeSeconds, must never depend on the build info being present.
			var entryAssembly = Assembly.GetEntryAssembly();

			var assemblyMetadata = entryAssembly?.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(k => k.Key, v => v.Value) ?? new Dictionary<string, string>();

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
