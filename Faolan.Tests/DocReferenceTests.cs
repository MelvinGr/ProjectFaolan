using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Doc-reference linter: every backtick-wrapped code symbol that looks like a type (a PascalCase name ending in a
	// strong type suffix - `FooService`, `BarTests`, `BazCodec`) and every repo-relative file path mentioned in docs/
	// must resolve to something that actually exists in the tree.
	//
	// This is the enforcement the prose citations lacked. The protocol reference tags facts with [Vn: ...] and
	// ProtocolCitationTests resolves each to a real artifact; but the subsystem / RE / tooling docs cite tests,
	// services and files in prose, and nothing checks those - so they rot silently as test classes are renamed and
	// files move. This test covers the prose style too, at zero authoring cost: you write docs exactly as before, and
	// a stale name fails the build.
	//
	// It is deliberately narrow to avoid false positives: only backtick symbols ending in a strong type suffix (broad
	// nouns are usually not types), and only paths under a repo top-dir (relative doc links are out of scope here).
	// Genuinely-retired references are allowlisted below with the reason.
	public class DocReferenceTests
	{
		// References intentionally kept for the historical record, each with its reason. A doc that documents removed
		// code (a before/after narrative) legitimately names symbols and paths that are not in the tree.
		private static readonly HashSet<string> Allowlist = new HashSet<string>(StringComparer.Ordinal)
		{
			// docs/re/spawn-dissection.md documents the retired Faolan.PacketAnalyzer approach as the
			// before-state of the construct-don't-replay work - these name that removed project's types/paths on purpose.
			"ArchetypeHarvester",
			"ArchiveDataImporter",
			"MapWorldBurstExtractor",
			"Faolan.PacketAnalyzer.Tests/ArchiveDataImporterTests.cs",
			"Faolan/Helpers/ArchiveDataImporter.cs",
			"tools/capture/extract_conversations.py"
		};

		private static readonly Regex TypeDef = new Regex(@"\b(?:class|struct|record|interface|enum)\s+([A-Z]\w+)", RegexOptions.Compiled);

		// A backtick `XxxSuffix` that is not a defined type is almost always a stale rename, not prose. Kept narrow
		// on purpose - a suffix here should read as "this is a class", not a common English word.
		private static readonly string[] TypeSuffixes =
		[
			"Tests", "Service", "Builder", "Codec", "Codecs", "Reader", "Catalog", "Listener", "Repository", "Importer", "Handler", "Dispatcher", "Scaffold", "Harvester", "Migrator",
			"Broadcaster", "Resolver", "Manager", "Simulation", "Extractor", "Framer"
		];

		[Fact]
		public void EveryDocSymbolAndPathReference_Resolves()
		{
			var repo = TestPaths.Repo();
			var defined = DefinedTypeNames(repo);

			var symbolRe = new Regex(@"`([A-Z]\w*(?:" + string.Join("|", TypeSuffixes) + @"))`", RegexOptions.Compiled);
			// Captures/ is deliberately not matched: those files live in the private data repo, so there is nothing here
			// to resolve them against, and a name list to check them against would ship an index of the private corpus in
			// every checkout. ProtocolCitationTests.V2Citations_AreWellFormed checks a citation's form instead.
			var pathRe = new Regex(@"((?:Faolan[\w.]*|docs|tools)/[\w./+-]+\.(?:cs|md|py|json|proto|bin|sh|slnx))", RegexOptions.Compiled);

			var docsDir = Path.Combine(repo, "docs");
			var failures = new List<string>();

			foreach (var file in Directory.EnumerateFiles(docsDir, "*.md", SearchOption.AllDirectories))
			{
				// docs/protocol/gen/ is generated from the code (never hand-edited); it can't rot independently.
				if (file.Contains(Path.Combine("protocol", "gen"), StringComparison.Ordinal))
					continue;

				var rel = Path.GetRelativePath(repo, file);
				var lines = File.ReadAllLines(file);
				for (var i = 0; i < lines.Length; i++)
				{
					foreach (Match m in symbolRe.Matches(lines[i]))
					{
						var sym = m.Groups[1].Value;
						if (!defined.Contains(sym) && !Allowlist.Contains(sym))
							failures.Add($"{rel}:{i + 1}  `{sym}` - no such type in the source (renamed/removed?)");
					}

					foreach (Match m in pathRe.Matches(lines[i]))
					{
						var path = m.Groups[1].Value;
						if (Allowlist.Contains(path))
							continue;

						if (!File.Exists(Path.Combine(repo, path)))
							failures.Add($"{rel}:{i + 1}  {path} - path does not exist");
					}
				}
			}

			Assert.True(failures.Count == 0, $"{failures.Count} stale doc reference(s) - fix the name/path, or add to DocReferenceTests.Allowlist with a reason:\n  " + string.Join("\n  ", failures));
		}

		// Every type name declared anywhere in the (non-build) source - the set a doc symbol must resolve into.
		private static HashSet<string> DefinedTypeNames(string repo)
		{
			var names = new HashSet<string>(StringComparer.Ordinal);
			foreach (var cs in Directory.EnumerateFiles(repo, "*.cs", SearchOption.AllDirectories))
			{
				if (cs.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
				    cs.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
					continue;

				foreach (Match m in TypeDef.Matches(File.ReadAllText(cs)))
					names.Add(m.Groups[1].Value);
			}

			return names;
		}
	}
}
