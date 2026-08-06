using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Gitea #674/#518: an RDB record that lives on the CDN (`le.idx file == 0xFF`) is still readable - `RdbReader`
	// fetches it through the Faolan.Web.Content mirror - but only if the caller ASKS for it. Two mistakes made that
	// silently not happen, both of which read as harmless:
	//
	//   * gating on `Entry.IsLocal`, which looks like "skip the unreadable ones" and means "skip everything that is
	//     not packed in NN.rdbdata";
	//   * building a bare `new RdbReader(rdbDir)`, which leaves the reader with no CDN source at all, so `CanRead`
	//     collapses back to `IsLocal`.
	//
	// Neither fails, warns, or shows up in a row count - the import just quietly covers less of the game. For several
	// types that is most of it: 577 of the 817 NPC spawn layouts (1010506), 580 of 653 in-game map tiles (1010013),
	// 276 of 520 tilemaps (1070003), 274 of 478 bounded-area records (1000053) and 44,106 of 95,289 render meshes
	// (1010001) are CDN-only. `rdb-npc-spawns` was importing 2,669 spawn areas where the client ships 3,970.
	//
	// So both are gated here, over the source itself. `IsLocal` has exactly one legitimate use left - REPORTING where
	// a resource physically lives - and that is what the allowlist is.
	public class RdbCdnGateTests
	{
		// Files whose `IsLocal` uses are the local/CDN split they exist to report, not a read gate.
		private static readonly HashSet<string> IsLocalReporters =
		[
			"RdbReader.cs", // the definition, plus ReadRaw's own local-vs-fetch dispatch and CanRead itself
			"RdbAssetExtractor.cs", // DescribeMesh / DescribeResource: the "local" | "cdn" strings in the diagnostics
			"ReportCli.cs" // the rdb-assets corpus summary's geomLocal/geomCdn + texLocal/texCdn tallies
		];

		// A test that constructs one deliberately without a mirror (there is none to reach in the suite), plus this
		// file, whose "offenders" are the literals the gate matches on.
		private static readonly HashSet<string> BareReaderAllowed = ["RdbCollisionMeshTests.cs", "RdbCdnGateTests.cs"];

		[Fact]
		public void NoRdbReader_GatesOnIsLocalOutsideTheReportersThatNameTheSplit()
		{
			var offenders = SourceFiles()
				.Where(f => !IsLocalReporters.Contains(Path.GetFileName(f)) && !BareReaderAllowed.Contains(Path.GetFileName(f)))
				.Select(f => (Name: Path.GetFileName(f), Text: File.ReadAllText(f)))
				// Scoped to files that actually touch the RDB: `IsLocal` is a common enough name that a travel
				// destination has one too, and flagging that would be a false positive with no fix.
				.Where(f => f.Text.Contains("RdbReader", StringComparison.Ordinal) && f.Text.Contains(".IsLocal", StringComparison.Ordinal))
				.Select(f => f.Name)
				.ToList();

			Assert.True(offenders.Count == 0,
				"`Entry.IsLocal` is not a readability test - a CDN record is readable through the mirror. Use `rdb.CanRead(e)` "
				+ "plus an empty-body guard (a CDN miss returns empty, it does not throw). Offenders: " + string.Join(", ", offenders));
		}

		// The other half: a reader built without a CDN base makes `CanRead` collapse into `IsLocal`, so the correct
		// gate stops helping. Every construction has to hand one over - `ctx.RdbProxyBase` in a module,
		// `ReportCli.OpenRdb` in a report, `IConfiguration.CdnMirrorAddress` in Faolan.Web.
		[Fact]
		public void EveryRdbReader_IsBuiltWithACdnSource()
		{
			var offenders = new List<string>();
			foreach (var file in SourceFiles().Where(f => !BareReaderAllowed.Contains(Path.GetFileName(f))))
			{
				var lines = File.ReadAllLines(file);
				for (var i = 0; i < lines.Length; i++)
				{
					var at = lines[i].IndexOf("new RdbReader(", StringComparison.Ordinal);
					if (at < 0)
						continue;

					// One argument = the rdbDir alone, i.e. no mirror. The call is always on one line in this tree.
					var args = lines[i][(at + "new RdbReader(".Length)..];
					if (!args.Contains(',', StringComparison.Ordinal))
						offenders.Add($"{Path.GetFileName(file)}:{i + 1}");
				}
			}

			Assert.True(offenders.Count == 0,
				"`new RdbReader(dir)` has no CDN source, so every on-demand record silently disappears. Pass the mirror base "
				+ "(ctx.RdbProxyBase / ReportCli.OpenRdb / config.CdnMirrorAddress). Offenders: " + string.Join(", ", offenders));
		}

		private static IEnumerable<string> SourceFiles()
		{
			foreach (var project in new[] { "Faolan.Core.Rdb", "Faolan.Importer", "Faolan.Web", "Faolan", "Faolan.Tests" })
			foreach (var file in Directory.EnumerateFiles(TestPaths.Repo(project), "*.cs", SearchOption.AllDirectories))
			{
				if (!file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
				    && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
					yield return file;
			}
		}
	}
}
