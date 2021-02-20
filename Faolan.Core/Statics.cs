using System;
using System.Diagnostics;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core
{
    public static class Statics
    {
        public const string ConfigFilePath = "faolan.json";

#if DEBUG
        public static bool IsDebug => true;
        public const string BuildConfig = "Debug";
#else
        public static bool IsDebug => false;
        public const string BuildConfig = "Release";
#endif

        public static readonly string BuildString = $"{GitVersionInformation.Sha} {BuildConfig} @ " +
                                                    FileVersionInfo.GetVersionInfo(typeof(Statics).Assembly.Location)
                                                        .Comments; // {RuntimeInformation.RuntimeIdentifier.ToTitleCase()} 

        public static string Banner =>
            "-------------------------------------------------------------------------------\n" +
            "|  ####  ####   ###    # #### ### #####    ####   #    ###  #      #   #   #  |\n" +
            "|  #   # #   # #   #   # #    #     #      #     # #  #   # #     # #  ##  #  |\n" +
            "|  ####  ####  #   #   # #### #     #      #### #   # #   # #    #   # # # #  |\n" +
            "|  #     #  #  #   #   # #    #     #      #    ##### #   # #    ##### #  ##  |\n" +
            "|  #     #   #  ###  ### #### ###   #      #    #   #  ###  #### #   # #   #  |\n" +
            "-------------------------------------------------------------------------------\n" +
            "| " + BuildString.PadBoth(75) + " |\n" +
            "-------------------------------------------------------------------------------\n";

        public static readonly (Race race, ArchType archType, Class clas)[] RaceArchClassCombinations =
        {
            (Race.Aquilonian, ArchType.Priest, Class.PriestOfMitra),
            (Race.Aquilonian, ArchType.Soldier, Class.Conqueror),
            (Race.Aquilonian, ArchType.Soldier, Class.DarkTemplar),
            (Race.Aquilonian, ArchType.Soldier, Class.Guardian),
            (Race.Aquilonian, ArchType.Rogue, Class.Barbarian),
            (Race.Aquilonian, ArchType.Rogue, Class.Ranger),
            (Race.Aquilonian, ArchType.Rogue, Class.Assassin),
            (Race.Cimmerian, ArchType.Priest, Class.BearShaman),
            (Race.Cimmerian, ArchType.Soldier, Class.Conqueror),
            (Race.Cimmerian, ArchType.Soldier, Class.DarkTemplar),
            (Race.Cimmerian, ArchType.Soldier, Class.Guardian),
            (Race.Cimmerian, ArchType.Rogue, Class.Barbarian),
            (Race.Cimmerian, ArchType.Rogue, Class.Ranger),
            (Race.Stygian, ArchType.Priest, Class.TempestOfSet),
            (Race.Stygian, ArchType.Rogue, Class.Ranger),
            (Race.Stygian, ArchType.Rogue, Class.Assassin),
            (Race.Stygian, ArchType.Mage, Class.HeraldOfXotli),
            (Race.Stygian, ArchType.Mage, Class.Demonologist),
            (Race.Stygian, ArchType.Mage, Class.Necromancer)
        };

        public static uint EpochTime()
        {
            return (uint) DateTimeOffset.Now.ToUnixTimeSeconds();
        }
    }
}