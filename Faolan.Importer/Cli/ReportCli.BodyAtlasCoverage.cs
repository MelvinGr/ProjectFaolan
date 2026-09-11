using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;

namespace Faolan.Importer.Cli
{
	// `--report body-atlas-coverage --report-arg <id>[,<id>...] [--out <dir>]`: how much of a body atlas the dilator's
	// heuristic covers, measured against the mesh's own UV triangles.
	//
	// The body-atlas dilator decides "is this texel background" from colour plus local flatness, and that guess fails
	// where the tan gutter meets tan skin - a smooth patch of upper chest passes both guards and gets flattened. The
	// mesh's own UV triangles answer the same question without guessing, and this report is what says the two agree
	// where the heuristic is right and where exactly it is wrong.
	//
	// It also settles the orientation, which is the one thing that could silently invert the whole result: it
	// rasterizes the coverage with V as-is and with `1 - v`, and prints how much of each lands on the heuristic's
	// islands.
	//
	// The id is any of the three a body is reached by - a CHARACTERCREATOR `<body monsterdataid>` (1091334 is the
	// Aquilonian female), a MESHINDEXFILE manifest, or a bare RenderMesh.
	public static partial class ReportCli
	{
		private static void BodyAtlasCoverage(string rdbDir, string? arg, string? outDir)
		{
			var ids = (arg ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(x => uint.TryParse(x, out var v)
					? v
					: 0u)
				.Where(x => x != 0)
				.ToList();
			if (ids.Count == 0)
			{
				Console.WriteLine("usage: --report body-atlas-coverage --report-arg <id>[,<id>...] [--out <dir>]");
				return;
			}

			using var rdb = OpenRdb(rdbDir);
			foreach (var id in ids)
				One(rdb, id, outDir);
		}

		private static void One(RdbReader rdb, uint id, string? outDir)
		{
			if (RdbAssetExtractor.DescribeBodyAtlasCoverage(rdb, id) is not { } b)
			{
				Console.WriteLine($"{id}: no body atlas (missing mesh index, render mesh, or diffuse)");
				return;
			}

			var n = b.Width * b.Height;
			Console.WriteLine($"=== {id}  atlas {b.Width}x{b.Height} ({n} texels), {b.SubMeshes} sub-meshes");
			Console.WriteLine($"  heuristic:       island {Pct(n - Count(b.HeuristicGutter), n)}   gutter {Pct(Count(b.HeuristicGutter), n)}");
			Report("UV coverage    ", b.Covered);
			Report("UV coverage 1-v", b.CoveredFlipped);

			// With --out, write the masks as images. The numbers say which orientation agrees with the heuristic; only
			// looking at one says whether the rasterizer drew a body or drew noise, and no ratio can tell you that.
			if (outDir != null)
			{
				Write($"{id}-uv.png", b.Covered);
				Write($"{id}-uv-flipped.png", b.CoveredFlipped);
				Write($"{id}-heuristic-island.png", b.HeuristicGutter.Select(g => !g).ToArray());
			}

			Console.WriteLine();
			return;

			void Report(string label, bool[]? covered)
			{
				if (covered == null)
				{
					Console.WriteLine($"  {label}: mesh carries no UVs");
					return;
				}

				// `onIsland` is how much of the coverage the heuristic already agreed was skin - high means the same
				// atlas regions, i.e. the right way up. `gutterCovered` is the fix: texels the heuristic was about to
				// flatten that the mesh actually draws.
				int covCount = 0, onIsland = 0, gutterCovered = 0, islandUncovered = 0;
				for (var i = 0; i < covered.Length; i++)
				{
					if (covered[i])
					{
						covCount++;
						if (b.HeuristicGutter[i])
							gutterCovered++;
						else
							onIsland++;
					}
					else if (!b.HeuristicGutter[i])
					{
						islandUncovered++;
					}
				}

				Console.WriteLine($"  {label}: covered {Pct(covCount, n)}, of which {Pct(onIsland, covCount)} on the heuristic's islands");
				Console.WriteLine($"      heuristic gutter the mesh DOES draw  : {Pct(gutterCovered, n)}   <- what the heuristic flattened");
				Console.WriteLine($"      heuristic island the mesh never draws: {Pct(islandUncovered, n)}");
			}

			void Write(string name, IReadOnlyList<bool>? mask)
			{
				if (mask == null)
					return;

				var rgba = new byte[n * 4];
				for (var i = 0; i < mask.Count; i++)
				{
					var v = mask[i]
						? (byte)255
						: (byte)0;
					rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = v;
					rgba[i * 4 + 3] = 255;
				}

				var path = Path.Combine(outDir!, name);
				File.WriteAllBytes(path, SkiaImage.EncodePng(rgba, b.Width, b.Height));
				Console.WriteLine($"      wrote {path}");
			}
		}

		private static int Count(IReadOnlyList<bool> mask)
		{
			var n = 0;
			foreach (var b in mask)
			{
				if (b)
					n++;
			}

			return n;
		}

		private static string Pct(int part, int whole)
		{
			return whole == 0
				? "0 (0.0%)"
				: $"{part} ({100.0 * part / whole:0.0}%)";
		}
	}
}
