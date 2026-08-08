using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Faolan.Tests.Support.ProtocolDocs
{
	// Best-effort text scan of Opcodes.cs for the trailing `// ...` comment on each enum member. ENRICHMENT ONLY:
	// reflection already gives the authoritative name + value on every page, so a missing or stale comment never
	// affects a generated page's correctness (the regeneration test still pins the whole page against reflection).
	public static class OpcodesSource
	{
		private static readonly Regex EnumRe = new Regex(@"public enum (\w+)\s*:");
		private static readonly Regex MemberRe = new Regex(@"^\s*(\w+)\s*=\s*0x[0-9A-Fa-f]+\s*,?\s*//\s*(.+?)\s*$");

		// (enumName, memberName) -> trailing comment, for every commented member in Opcodes.cs.
		public static Dictionary<(string Enum, string Member), string> AllNotes()
		{
			var map = new Dictionary<(string, string), string>();
			var src = File.ReadAllText(TestPaths.Repo("Faolan.Core", "Enums", "Opcodes.cs"));

			string? currentEnum = null;
			foreach (var line in src.Split('\n'))
			{
				var em = EnumRe.Match(line);
				if (em.Success)
				{
					currentEnum = em.Groups[1].Value;
					continue;
				}

				if (currentEnum == null)
					continue;

				var mm = MemberRe.Match(line);
				if (mm.Success)
					map[(currentEnum, mm.Groups[1].Value)] = mm.Groups[2].Value;
			}

			return map;
		}

		// memberName -> comment, for one enum.
		public static Dictionary<string, string> Notes(string enumName)
		{
			var map = new Dictionary<string, string>();
			foreach (var ((e, m), note) in AllNotes())
			{
				if (e == enumName)
					map[m] = note;
			}

			return map;
		}
	}
}
