using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Importer.Captures;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	public static partial class ReportCli
	{
		// `--report sql-seed [--report-arg <dir>]`: the dialect-neutral SQL seed - the game data as one file
		// that loads into SQLite and Postgres alike. Writes to the repo root by default, which is where faolan.sql is
		// committed.
		private static async Task<int> SqlSeed(string? outputDir)
		{
			var directory = outputDir ?? ".";
			await using var db = OpenDb();
			var body = Path.Combine(directory, "faolan.sql");
			var (tables, rows) = SqlSeedWriter.Write(db, body);
			Console.WriteLine($"wrote {body} ({tables} tables, {rows} rows)");
			return 0;
		}

		// `--report apply-seed [--dry-run]`: upgrade an EXISTING database's game content from faolan.sql.
		// The seed only loads itself into an empty database, so this is how a database you have been playing on picks
		// up newer content - as a merge that leaves characters and everything else player-owned alone.
		private static int ApplySeed()
		{
			var dryRun = _options.DryRun;
			using var db = OpenDb();
			var report = SqlSeedApplier.Apply(db, Global.SeedPath, dryRun);

			foreach (var change in report.Changes.Where(c => c.Any).OrderByDescending(c => c.Inserted + c.Updated + c.Deleted))
				Console.WriteLine($"  {change.Table,-28} +{change.Inserted} ~{change.Updated} -{change.Deleted}");

			if (report.Changes.All(c => !c.Any))
				Console.WriteLine("  the database already matches the seed - nothing to do.");

			if (report.Blocked)
			{
				Console.Error.WriteLine();
				Console.Error.WriteLine("REFUSED: the new seed drops rows that are still referenced from outside it:");
				foreach (var blocker in report.Blockers)
					Console.Error.WriteLine($"  {blocker}");

				Console.Error.WriteLine("Nothing was written. Resolve those references (or keep the rows) and re-run - deleting");
				Console.Error.WriteLine("someone's character data to make a content upgrade fit is not a call this tool makes.");
				return 1;
			}

			Console.WriteLine(dryRun
				? $"DRY RUN - would apply +{report.Inserted} ~{report.Updated} -{report.Deleted} rows."
				: $"applied +{report.Inserted} ~{report.Updated} -{report.Deleted} rows.");
			return 0;
		}

		// Rewrites the content between the named markers (<!-- marker:start -->/<!-- marker:end -->) in README.md,
		// leaving the rest untouched.
		private static void UpdateReadmeSection(string marker, string block)
		{
			var path = "README.md"; // CWD-relative: the importer runs from the repo root
			var text = File.ReadAllText(path);
			var start = $"<!-- {marker}:start -->";
			var end = $"<!-- {marker}:end -->";
			var s = text.IndexOf(start, StringComparison.Ordinal);
			var e = text.IndexOf(end, StringComparison.Ordinal);
			if (s < 0 || e < 0 || e < s)
			{
				Console.WriteLine($"README.md has no {start}/{end} markers; skipping.");
				return;
			}

			File.WriteAllText(path, text[..(s + start.Length)] + "\n" + block + "\n" + text[e..]);
		}

		// The stand-in wire handle the WorldObj diagnostic builds with - see the note at its use.
		private const uint DiagnosticHandle = 0x00000001;

		// Build a named world object via the burstless world-entry path and print its hex (render diffing).
		private static async Task WorldObj(string target)
		{
			await using var db = OpenDb();
			foreach (var row in await db.WorldObjects.Where(o => o.Name!.Contains(target)).ToListAsync())
			{
				byte[]? baseBody = null;
				var baseOk = false;
				try
				{
					// Any handle will do: this prints a body for eyeballing/diffing the RENDER, and the handle is live
					// state the zone mints at spawn (see LiveWorldObject), so no row carries one to be faithful to.
					baseBody = WorldObjectCodec.Build(row, DiagnosticHandle);
					baseOk = true;
				}
				catch (InvalidOperationException)
				{
					// Un-lifted row - this diagnostic REPORTS buildability per row, so catch the fail-fast throw and
					// record it as a failure instead of aborting the sweep.
				}

				var rewriteOk = baseOk && SpawnTransformPatch.TryRewritePlayfield(baseBody!, 0x01EA985Cu);
				Console.WriteLine($"map {row.MapId} {row.Name} baseBuild={baseOk} rewrite={rewriteOk} asset={row.GeometryAssetId}/{row.GeometryAssetType} words={row.Words.Count}");
				if (baseOk)
					Console.WriteLine("  BUILT: " + Convert.ToHexString(baseBody!).ToLowerInvariant());
			}
		}
	}
}
