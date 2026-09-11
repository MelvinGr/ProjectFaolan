using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Importer.Cli;
using Faolan.Importer.Rdb;

namespace Faolan.Importer.Modules
{
	/// <summary>
	///     Combo catalog (name / unlock level / feat id / actions /...) from the RDB 1010206 plaintext-XML combo definitions.
	/// </summary>
	public sealed class RdbCombosModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Combo catalog (name/level/featid/actions) from the RDB 1010206 plaintext XML.";
		public string Name => "rdb-combos";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (seen, imported) = await RdbComboImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);
			return new ImportResult($"{seen} combo XML entries, {imported} combos imported.");
		}
	}
}
