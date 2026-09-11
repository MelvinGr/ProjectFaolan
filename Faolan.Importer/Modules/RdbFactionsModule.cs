using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Importer.Cli;
using Faolan.Importer.Rdb;

namespace Faolan.Importer.Modules
{
	/// <summary>
	///     The Khitai faction catalog and its kill-payout matrix. Opt-in like every RDB module, and dependency-free: it
	///     is keyed by the client's own faction ids and joins to nothing we import.
	/// </summary>
	public sealed class RdbFactionsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Khitai faction catalog + kill-payout matrix (FactionRelations.xml 1010701 + LDB 254).";
		public string Name => "rdb-factions";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var r = await RdbFactionImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);
			return new ImportResult($"{r.Factions} faction(s) ({r.Named} named from the LDB, {r.Unjoined} unjoined), {r.Awards} kill-award row(s).");
		}
	}
}
