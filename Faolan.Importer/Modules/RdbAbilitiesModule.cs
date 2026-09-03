using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Rdb;
using Faolan.Importer.Cli;
using Faolan.Importer.Extractors;
using Faolan.Importer.Rdb;

namespace Faolan.Importer.Modules
{
	// Ability names from the RDB 1000020 templates, decrypted with each ability's own harvested Data1..4 TEA key.
	public sealed class RdbAbilitiesModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(AbilitiesModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Ability names from the RDB 1000020 templates (TEA-decrypted with the 0x2052 key).";
		public string Name => "rdb-abilities";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (decrypted, namesSet) = await RdbAbilityImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);
			return new ImportResult($"{decrypted} templates decrypted, {namesSet} names set.");
		}
	}
}
