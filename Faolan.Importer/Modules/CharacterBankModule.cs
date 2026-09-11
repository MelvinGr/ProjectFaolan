using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Protobuf;
using Faolan.Core.Rdb;
using Faolan.Importer.Captures;
using Faolan.Importer.Cli;
using Faolan.Importer.Extractors;
using Faolan.Importer.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Modules
{
	/// <summary>
	///     Per-character personal bank (CharacterInventory container kind 105) from the on-demand BankIIR packets; the
	///     bank is not in the login snapshot, so it needs its own pass. Needs characters (attribution) and items (the
	///     templates the bank references).
	/// </summary>
	public sealed class CharacterBankModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(CharactersModule), nameof(ItemsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Per-character bank contents (BankIIR, kind 105).";
		public string Name => "character-bank";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (chars, items) = await CaptureBankImporter.ImportAsync(ctx.Db, ctx.RawPackets);
			return new ImportResult($"{chars} characters, {items} bank items.");
		}
	}
}
