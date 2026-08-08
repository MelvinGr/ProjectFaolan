using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// P3 of docs/protocol/methodology.md - the citation linter. It is the whole difference between a doc that CLAIMS to be
	// verified and one that IS: every verification tag in the protocol reference must name an artifact that exists.
	//
	//   [V3: BurstGoldenTests.CreationScriptBurst_IsByteStable]   -> a real xUnit test method
	//   [V2: <character>_<scenario>.tshark.json frame 366]        -> a well-formed capture file name (see below)
	//   [V1: tools/ghidra/DumpRpcTables.java]                     -> a real repo path
	//   [V0: ...]                                                 -> must be listed in docs/protocol/gaps.md
	//
	// A stale citation is worse than none - it looks authoritative. This test already caught three: methodology.md's own
	// examples cited a test method deleted with SendWorldPrepBurst, a capture filename with a literal "...", and a
	// Ghidra script (DumpRpcMethods.java) that never existed.
	//
	// Placeholders (`[V3: …]`) in prose that DESCRIBES the format are skipped, not linted.
	public class ProtocolCitationTests
	{
		private static readonly Regex Citation = new Regex(@"\[V([0-3]):\s*([^\]]+)\]");

		// Guards the linter itself: if the regex or the docs path breaks, the three tests above pass vacuously.
		[Fact]
		public void Linter_FindsCitationsToCheck()
		{
			var all = Citations().ToList();
			Assert.True(all.Count >= 3, $"only {all.Count} citations found - the linter is probably not scanning docs/");
		}

		// V0 = unproven: methodology.md §1 requires every V0 to be tracked in gaps.md, so a gap can never go silent.
		[Fact]
		public void V0Citations_AreListedInGapsMd()
		{
			var v0 = Citations().Where(c => c.Level == 0).ToList();
			if (v0.Count == 0)
				return; // nothing unproven is tagged yet (gaps.md lands in P6)

			var gapsPath = TestPaths.Repo("docs", "protocol", "gaps.md");
			Assert.True(File.Exists(gapsPath), $"{v0.Count} V0 citations exist but docs/protocol/gaps.md does not");

			var gaps = File.ReadAllText(gapsPath);
			var bad = v0.Where(c => !gaps.Contains(c.Payload, StringComparison.Ordinal)).Select(c => $"{c.File}:{c.Line}  [V0: {c.Payload}] - not listed in gaps.md").ToList();

			Assert.True(bad.Count == 0, "Every V0 must be tracked in gaps.md:\n  " + string.Join("\n  ", bad));
		}

		// V1 = client-RE: the payload is a real repo-relative path (a Ghidra script or recovered artifact).
		[Fact]
		public void V1Citations_NameARealRepoPath()
		{
			var bad = (from c in Citations()
				where c.Level == 1
				let path = c.Payload.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]
				where !File.Exists(TestPaths.Repo(path.Split('/')))
				select $"{c.File}:{c.Line}  [V1: {c.Payload}] - no such file: {path}").ToList();

			Assert.True(bad.Count == 0, "V1 citations must name a real repo path:\n  " + string.Join("\n  ", bad));
		}

		// V2 = capture-observed: the payload names a capture file (optionally "... frame N").
		//
		// This checks the citation's FORM, not that the file exists. It used to resolve the name against a list of all
		// 227 corpus file names carried in the committed extract's manifest - which meant every checkout shipped an
		// index of the private capture repo. The names are gone; the corpus lives only in the data repo, and the tests
		// no longer reach for it. What survives is what this linter actually caught: a payload that is not a capture
		// filename at all (methodology.md once cited a literal "..."), which is the stale-citation shape that matters.
		// Whether the named capture exists is verified by whoever has the corpus, not by the suite.
		[Fact]
		public void V2Citations_AreWellFormed()
		{
			var bad = (from c in Citations()
				where c.Level == 2
				let name = c.Payload.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]
				where !IsCaptureFileName(name)
				select $"{c.File}:{c.Line}  [V2: {c.Payload}] - {name} is not a capture file name").ToList();

			Assert.True(bad.Count == 0, "V2 citations must open with a capture file name (*.tshark.json / *.pcap / *.pcapng):\n  " + string.Join("\n  ", bad));
		}

		// V3 = byte-exact: the payload names a real xUnit test method (`ClassName.MethodName`).
		[Fact]
		public void V3Citations_NameARealTestMethod()
		{
			var testTypes = typeof(ProtocolCitationTests).Assembly.GetTypes().ToLookup(t => t.Name, StringComparer.Ordinal);
			var bad = new List<string>();

			foreach (var (file, line, level, payload) in Citations().Where(c => c.Level == 3))
			{
				var token = payload.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
				var dot = token.LastIndexOf('.');
				if (dot <= 0)
				{
					bad.Add($"{file}:{line}  [V3: {payload}] is not of the form ClassName.MethodName");
					continue;
				}

				var (cls, method) = (token[..dot], token[(dot + 1)..]);
				var candidates = testTypes[cls].ToList();
				if (candidates.Count == 0)
				{
					bad.Add($"{file}:{line}  [V3: {payload}] - no test class named {cls}");
					continue;
				}

				var m = candidates.SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)).FirstOrDefault(m => m.Name == method);
				if (m == null)
					bad.Add($"{file}:{line}  [V3: {payload}] - {cls} has no method {method}");
				else if (!m.GetCustomAttributes().Any(a => a.GetType().Name is "FactAttribute" or "TheoryAttribute"))
					bad.Add($"{file}:{line}  [V3: {payload}] - {cls}.{method} is not a [Fact]/[Theory]");
			}

			Assert.True(bad.Count == 0, "V3 citations must name a real byte-exact test:\n  " + string.Join("\n  ", bad));
		}

		private static IEnumerable<(string File, int Line, int Level, string Payload)> Citations()
		{
			var docs = TestPaths.Repo("docs");
			foreach (var file in Directory.EnumerateFiles(docs, "*.md", SearchOption.AllDirectories))
			{
				// archive/ is frozen history, not part of the live reference.
				if (file.Contains($"{Path.DirectorySeparatorChar}archive{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
					continue;

				var lines = File.ReadAllLines(file);
				for (var i = 0; i < lines.Length; i++)
				{
					foreach (Match m in Citation.Matches(lines[i]))
					{
						var payload = m.Groups[2].Value.Trim();
						if (IsPlaceholder(payload))
							continue;

						yield return (Path.GetRelativePath(TestPaths.Repo(), file), i + 1, int.Parse(m.Groups[1].Value), payload);
					}
				}
			}
		}

		// A capture file name: one of the corpus' extensions, and no ellipsis standing in for the real name.
		private static bool IsCaptureFileName(string name)
		{
			return (name.EndsWith(".tshark.json", StringComparison.Ordinal) || name.EndsWith(".pcapng", StringComparison.Ordinal) || name.EndsWith(".pcap", StringComparison.Ordinal)) &&
			       !name.Contains("...", StringComparison.Ordinal) && !name.Contains('…', StringComparison.Ordinal);
		}

		// Prose that documents the citation FORM rather than making a claim.
		private static bool IsPlaceholder(string payload)
		{
			return payload is "…" or "..." || payload.StartsWith("<", StringComparison.Ordinal);
		}
	}
}
