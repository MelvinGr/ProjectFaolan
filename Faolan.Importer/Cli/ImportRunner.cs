using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Faolan.Importer.Cli
{
	/// <summary>
	///     Runs a resolved module plan inside one transaction, prints each module's summary and a full table delta,
	///     and either commits or, under --dry-run, rolls the whole thing back.
	/// </summary>
	/// <remarks>
	///     Migrations must already have run: they cannot execute inside a user transaction, so the caller migrates
	///     before invoking this.
	/// </remarks>
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

			for (var i = 0; i < plan.Count; i++)
			{
				var module = plan[i];
				var mode = ctx.ModeFor(module.Name);
				if (!module.SupportedModes.Contains(mode))
				{
					// Deterministic, and deliberately not SupportedModes.First(): that is a HashSet, so its order is
					// unspecified and the fallback could differ between runs. The preference order is Upsert (refresh
					// in place, the closest thing to any request), then Overwrite, then Insert.
					var fallback = new[] { WriteMode.Upsert, WriteMode.Overwrite, WriteMode.Insert }.First(module.SupportedModes.Contains);
					await ctx.Out.WriteLineAsync($"[{module.Name}] WARNING: mode '{mode}' unsupported; using '{fallback}'.");
					mode = fallback;
				}

				// Record it so the module sees the mode it is actually running in - it branches on ModeFor.
				ctx.SetEffectiveMode(module.Name, mode);

				ImportResult result;
				try
				{
					result = await module.RunAsync(ctx);
				}
				catch (Exception e)
				{
					// The plan is one transaction, so a module throwing must abort everything: a half-applied plan is
					// not a state to commit, and swallowing the failure to carry on would be a silent fallback. Name
					// the module that died and the ones it took with it, then rethrow - without that the failure
					// surfaces as a bare exception and the modules sequenced after it go missing with nothing said.
					var skipped = plan.Skip(i + 1).Select(m => m.Name).ToList();
					await ctx.Out.WriteLineAsync($"[{module.Name}] FAILED: {e.GetType().Name}: {e.Message}");
					await ctx.Out.WriteLineAsync(skipped.Count == 0
						? "Nothing was written - the transaction is rolled back. It was the last module in the plan."
						: $"Nothing was written - the transaction is rolled back. {skipped.Count} module(s) never ran: {string.Join(", ", skipped)}");
					throw;
				}

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
