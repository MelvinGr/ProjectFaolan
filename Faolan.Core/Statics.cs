using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Faolan.Core
{
	public static class Statics
	{
#if DEBUG
		public static bool IsDebug => true;
#else
        public static bool IsDebug => false;
#endif

		public static readonly string BuildConfiguration;
		public static readonly DateTime BuildDateTimeUtc;
		public static readonly string BuildString;
		public static readonly string StartupBanner;

		static Statics()
		{
			var entryAssembly = Assembly.GetEntryAssembly()!;

			var assemblyMetadata = entryAssembly
				.GetCustomAttributes<AssemblyMetadataAttribute>()
				.ToDictionary(k => k.Key, v => v.Value);

			BuildConfiguration = assemblyMetadata["BuildConfiguration"];
			BuildDateTimeUtc = DateTime.Parse(assemblyMetadata["BuildDateTimeUtc"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

			// The SDK appends "+<commit-sha>" to the informational version (IncludeSourceRevisionInInformationalVersion),
			// so the HEAD SHA rides along for free - no custom metadata needed.
			var informationalVersion = entryAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
			var plusIndex = informationalVersion.IndexOf('+');
			var gitSha = plusIndex >= 0 ? informationalVersion[(plusIndex + 1)..] : informationalVersion;

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

		public static readonly (Race race, ArchType archType, Class clas)[] RaceArchClassCombinations =
		[
			(Race.Aquilonian, ArchType.Priest, Class.PriestOfMitra), (Race.Aquilonian, ArchType.Soldier, Class.Conqueror), (Race.Aquilonian, ArchType.Soldier, Class.DarkTemplar),
			(Race.Aquilonian, ArchType.Soldier, Class.Guardian), (Race.Aquilonian, ArchType.Rogue, Class.Barbarian), (Race.Aquilonian, ArchType.Rogue, Class.Ranger), (Race.Aquilonian, ArchType.Rogue, Class.Assassin),
			(Race.Cimmerian, ArchType.Priest, Class.BearShaman), (Race.Cimmerian, ArchType.Soldier, Class.Conqueror), (Race.Cimmerian, ArchType.Soldier, Class.DarkTemplar), (Race.Cimmerian, ArchType.Soldier, Class.Guardian),
			(Race.Cimmerian, ArchType.Rogue, Class.Barbarian), (Race.Cimmerian, ArchType.Rogue, Class.Ranger), (Race.Stygian, ArchType.Priest, Class.TempestOfSet), (Race.Stygian, ArchType.Rogue, Class.Ranger),
			(Race.Stygian, ArchType.Rogue, Class.Assassin), (Race.Stygian, ArchType.Mage, Class.HeraldOfXotli), (Race.Stygian, ArchType.Mage, Class.Demonologist), (Race.Stygian, ArchType.Mage, Class.Necromancer)
		];

		public static IServiceCollection ConfigureFaolanDatabase(this IServiceCollection services, IConfiguration configuration)
		{
			// Game-server persistence model: live state lives in memory (the session); the DB is a persistence
			// boundary touched only at save/load points. So contexts are SHORT-LIVED, one per unit of work, created
			// from a pooled factory - never held open across a connection or game tick (which would leak/stale and
			// is not thread-safe). The repository owns all context lifetime via this factory.
			services.AddPooledDbContextFactory<DatabaseContext>(options => { options.UseSqlite($"Data Source={configuration.DatabasePath}", x => x.MigrationsAssembly("Faolan")); });

			services.AddSingleton<IDatabaseRepository, DatabaseRepository>();

			return services;
		}
	}
}
