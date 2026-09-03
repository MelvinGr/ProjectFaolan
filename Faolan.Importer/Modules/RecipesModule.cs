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
	// Crafting recipe-book catalog (TradeskillReplyIIR 0x9062D559) - the list served when the client opens the
	// Recipes window. Opt-in (its only capture is one L80 char's full 603-recipe book).
	public sealed class RecipesModule : IImportModule
	{
		// Depends on `items`: every RecipeItem.ItemId is a hard FK to Items. The recipe importer harvests each
		// referenced item's identity (key + hash) into Items from the recipe element, but the items module must run
		// first so a real named item wins over the bare harvested placeholder (and the FK resolves for a `--only
		// recipes` run, which pulls items in via this dependency).
		public IReadOnlyList<string> DependsOn => [nameof(ItemsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;
		public bool InDefaultSet => false;

		public string Description => "Crafting recipes book (TradeskillReplyIIR TradeskillReplyCodec.Opcode, opt-in).";
		public string Name => "recipes";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var recipes = ctx.Captures.SelectMany(c => CaptureRecipeImporter.Extract(c.Packets)).ToList();
			var deduped = recipes.GroupBy(r => r.Id).Select(g => g.First()).ToList();
			var (seen, added, updated, harvested) = await CaptureRecipeImporter.ImportAsync(ctx.Db, deduped);
			return new ImportResult($"{seen} recipe(s), {added} new, {updated} updated, {harvested} item template(s) harvested.");
		}
	}
}
