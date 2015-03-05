using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace LibFaolan.Other
{
    public static class Statics
    {
        public static string AssemblyDescription =>
            typeof (Statics).Assembly.GetCustomAttributes(typeof (AssemblyDescriptionAttribute), false)
                .Cast<AssemblyDescriptionAttribute>().First().Description;

        public static string BuildHash => AssemblyDescription.Split(' ')[0];

        public static string BuildDate => DateTime.Parse(AssemblyDescription.Split(' ')[1])
            .ToString("HH:mm:ss MM/dd/yyyy", CultureInfo.InvariantCulture);

        public static string BuildInfo => "Build on: " + BuildDate + " Git rev: " + BuildHash;
    }
}
