using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Faolan.Importer.Cli
{
	// Runs a resolved module plan inside ONE transaction, prints each module's summary + a full table delta,
	// and either commits or (under --dry-run) rolls the whole thing back. Migrations must already have run
	// (they can't execute inside a user transaction) - the caller migrates before invoking this.
	public static class ImportRunner
	{
		public static async Task<int> RunAsync(ImportContext ctx, IReadOnlyList<IImportModule> plan)
		{
			await ctx.Out.WriteLineAsync($"Plan ({plan.Count} modules): {string.Join(" -> ", plan.Select(m => m.Name))}");
			await ctx.Out.WriteLineAsync($"Mode: {ctx.Mode}{(ctx.ModeOverrides.Count > 0 ? " (" + string.Join(", ", ctx.ModeOverrides.Select(o => $"{o.Key}={o.Value}")) + ")" : "")}" + (ctx.DryRun
				? "   [DRY-RUN - nothing will be written]"
				: ""));

			var before = await TableSnapshot.CaptureAsync(ctx.Db);

			await using var tx = await ctx.Db.Database.BeginTransactionAsync();

			foreach (var module in plan)
			{
				var mode = ctx.ModeFor(module.Name);
				if (!module.SupportedModes.Contains(mode))
				{
					// Deterministic, and NOT SupportedModes.First() - that is a HashSet, so its order is unspecified
					// and the fallback could differ between runs. Preference order is Upsert (refresh in place, the
					// closest thing to any request), then Overwrite, then Insert.
					var fallback = new[] { WriteMode.Upsert, WriteMode.Overwrite, WriteMode.Insert }.First(module.SupportedModes.Contains);
					await ctx.Out.WriteLineAsync($"[{module.Name}] WARNING: mode '{mode}' unsupported; using '{fallback}'.");
					mode = fallback;
				}

				// Record it so the module sees the mode it is actually running in - it branches on ModeFor.
				ctx.SetEffectiveMode(module.Name, mode);

				var result = await module.RunAsync(ctx);
				await ctx.Out.WriteLineAsync($"[{module.Name}] {result.Summary}");
			}

			var after = await TableSnapshot.CaptureAsync(ctx.Db);
			TableSnapshot.PrintDelta(before, after, ctx.Out);

			if (ctx.DryRun)
			{
				await tx.RollbackAsync();
				await ctx.Out.WriteLineAsync("\n[dry-run] transaction rolled back - nothing written.");
			}
			else
			{
				await tx.CommitAsync();
				await ctx.Out.WriteLineAsync("\nCommitted.");
			}

			return 0;
		}
	}
}
