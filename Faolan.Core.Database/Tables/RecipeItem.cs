using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;

namespace Faolan.Core.Database.Tables
{
	// One ingredient (input) or result (output) item of a crafting Recipe - a queryable projection of the recipe
	// record's KEY1 (ingredients) / listC (results) element lists (decoded in TradeskillRecipeCodec.ParseRecordItems,
	// see docs/subsystems/tradeskill-recipes.md). The recipe still ships byte-exact from Recipe.Data; these rows exist so
	// recipes are queryable ("which recipes produce / consume item X"). Each references the item template by id (hard
	// FK to Items) - the item's TEA key (the same key RequestRecipeInfo 0x2090 uses) and hash live there, harvested
	// into Items from the recipe element by the recipes importer, so they're not duplicated here.
	public class RecipeItem : BaseEntity
	{
		public uint ItemId { get; set; }
		[ForeignKey(nameof(ItemId))]
		public virtual Item Item { get; set; }

		public uint RecipeId { get; set; }
		[ForeignKey(nameof(RecipeId))]
		public virtual Recipe Recipe { get; set; }

		public int Position { get; set; } // order within its list

		[ConstrainToEnumValues]
		public RecipeItemRole Role { get; set; } // ingredient (input) or result (output)

		public uint Flag { get; set; } // element field [3] (small enum-ish; 0x14/0x28/...)

		public uint Trailing { get; set; } // ingredient trailing word [17] (0 for results)
	}

	public enum RecipeItemRole : byte
	{
		Ingredient = 0,
		Result = 1
	}
}
