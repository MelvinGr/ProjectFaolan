using System.Reflection;

[assembly: AssemblyTitle("Project Faolan")]
[assembly: AssemblyCompany("Project Faolan")]
[assembly: AssemblyCopyright("Copyright © Project Faolan 2015")]

#if DEBUG
[assembly: AssemblyConfiguration("Debug")]
#else
[assembly: AssemblyConfiguration("Release")]
#endif

[assembly: AssemblyVersion("0.0.$REVNUM$.$DIRTY$")]
[assembly: AssemblyDescription("$REVID$ $DATETIME$")]