using System.IO;

namespace Faolan.Core
{
	// Locates the repository root AT RUNTIME so no absolute build-machine path (e.g. /home/<user>/...) is ever baked
	// into source or config. Walks up from the current working directory (the dev tools - server, importer, web -
	// all run with CWD = repo root) and then from the executing assembly's directory, looking for the solution file
	// that marks the root. Falls back to the CWD if the marker isn't found (e.g. a published single-file deploy).
	public static class RepoPaths
	{
		private const string RootMarker = "ProjectFaolan.slnx";

		public static string Root { get; } = FindRoot();

		public static string Combine(string relative)
		{
			return Path.Combine(Root, relative);
		}

		private static string FindRoot()
		{
			foreach (var start in new[] { Directory.GetCurrentDirectory(), System.AppContext.BaseDirectory })
			{
				for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
					if (File.Exists(Path.Combine(dir.FullName, RootMarker)))
						return dir.FullName;
			}

			return Directory.GetCurrentDirectory();
		}
	}
}
