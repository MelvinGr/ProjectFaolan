using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Importer.Captures;
using Faolan.Importer.Cli;

namespace Faolan.Importer.Modules
{
	/// <summary>
	///     Crafting recipe-book catalog: the list served when the client opens the Recipes window. Opt-in, because its
	///     only capture is one level 80 character's full 603-recipe book.
	/// </summary>
	public sealed class RecipesModule : IImportModule
	{
		/// <remarks>
		///     Every RecipeItem.ItemId is a hard foreign key to Items. The recipe importer harvests each referenced
		///     item's identity (key + hash) into Items from the recipe element, but the items module must run first so
		///     that a real named item wins over the bare harvested placeholder. The dependency also pulls items in for
		///     an <c>--only recipes</c> run, so the foreign key resolves there too.
		/// </remarks>
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
