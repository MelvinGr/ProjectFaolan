using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Importer.Captures;
using Faolan.Importer.Cli;

namespace Faolan.Importer.Modules
{
	/// <summary>
	///     Observed live character state: wallet balances and hotbar slots. Needs the characters to exist.
	/// </summary>
	public sealed class CharacterStateModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(CharactersModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Wallet balances + hotbar slots from captures.";
		public string Name => "character-state";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (money, shortcuts) = await CaptureCharacterStateImporter.ImportAsync(ctx.Db, ctx.Captures.Select(c => c.Packets));
			return new ImportResult($"{money} wallets, {shortcuts} hotbar slots seeded.");
		}
	}
}
