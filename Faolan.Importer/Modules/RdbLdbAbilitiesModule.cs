using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Rdb;
using Faolan.Importer.Cli;
using Faolan.Importer.Extractors;
using Faolan.Importer.Rdb;

namespace Faolan.Importer.Modules
{
	/// <summary>
	///     The abilities the client knows but no capture ever granted, named from the English LDB page. Identity only:
	///     see RdbLdbAbilityImporter for why a row from here cannot be granted (the 0x2052 grant carries the TEA key,
	///     and the keys are random).
	/// </summary>
	public sealed class RdbLdbAbilitiesModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(RdbLdbNamesModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Catalog every ability the client knows, named from the English LDB page (identity only - not grantable).";
		public string Name => "rdb-ldb-abilities";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var r = await RdbLdbAbilityImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);
			return new ImportResult($"{r.Total} abilities in the client; {r.AlreadyKnown} already catalogued, {r.Added} added (identity only).");
		}
	}
}
