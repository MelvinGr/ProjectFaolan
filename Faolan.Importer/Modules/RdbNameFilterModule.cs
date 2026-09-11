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
	///     The client's forbidden-word list for character names (RDB 1065100), stored hashed.
	/// </summary>
	public sealed class RdbNameFilterModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert,
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "The client's forbidden-word list for character names (RDB 1065100), stored HASHED.";
		public string Name => "rdb-name-filter";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var r = await RdbNameFilterImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase, ctx.ModeFor(Name) == WriteMode.Overwrite);
			return new ImportResult($"{r.Shipped} terms shipped, {r.Usable} usable after dedupe; {r.Added} added.");
		}
	}
}
