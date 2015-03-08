using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace LibFaolan.Other
{
    public static class Statics
    {
        public static IEnumerable<T> GetAttributes<T>() =>
            typeof(Statics).Assembly.GetCustomAttributes(typeof(T), false).Cast<T>();

        public static string AssemblyDescription => 
            GetAttributes<AssemblyDescriptionAttribute>().First().Description;

        public static string AssemblyConfiguration => 
            GetAttributes<AssemblyConfigurationAttribute>().First().Configuration;

        public static string BuildHash => AssemblyDescription.Split(' ')[0];
        public static bool BuildDirty => (AssemblyDescription.Split(' ')[1] == "1");

        public static string BuildDate => DateTime.Parse(AssemblyDescription.Split(' ')[2])
            .ToString("HH:mm:ss MM/dd/yyyy", CultureInfo.InvariantCulture);

        public static string BuildInfo => "Build on: " + BuildDate + " Git rev: " + BuildHash
            + (BuildDirty ? " (dirty)" : "") + " (" + AssemblyConfiguration + ")";
    }
}