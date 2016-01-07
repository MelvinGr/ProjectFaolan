namespace Faolan.Core
{
    public static class Statics
    {
#if DEBUG
        public static string BuildConfig = "Debug";
#else
        public static string BuildConfig = "Release";
#endif

        public static string BuildInfo => GitVersionInformation.InformationalVersion + $" ({BuildConfig})";

        public static string Banner =>
            "-------------------------------------------------------------------------------\n" +
            "|  ####  ####   ###    # #### ### #####    ####   #    ###  #      #   #   #  |\n" +
            "|  #   # #   # #   #   # #    #     #      #     # #  #   # #     # #  ##  #  |\n" +
            "|  ####  ####  #   #   # #### #     #      #### #   # #   # #    #   # # # #  |\n" +
            "|  #     #  #  #   #   # #    #     #      #    ##### #   # #    ##### #  ##  |\n" +
            "|  #     #   #  ###  ### #### ###   #      #    #   #  ###  #### #   # #   #  |\n" +
            "-------------------------------------------------------------------------------\n" +
            "| " + BuildInfo + " |\n" +
            "-------------------------------------------------------------------------------\n";
    }
}