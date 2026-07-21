using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;

namespace Faolan.Core.Database.Tables
{
	// One ingredient (input) or result (output) item of a crafting Recipe - the recipe record's KEY1 (ingredients) /
	// listC (results) element lists (TradeskillRecipeCodec elements, see docs/subsystems/tradeskill-recipes.md). These
	// rows ARE the wire elements (the recipe rebuilds byte-exact from them via TradeskillRecipeCodec.ToRecord) and are
	// also queryable ("which recipes produce / consume item X"). Each references the item template by id (hard FK to
	// Items) - the item's TEA key (the same key RequestRecipeInfo 0x2090 uses) and hash live there, harvested into
	// Items from the recipe element by the recipes importer, so they're not duplicated here.
	public class RecipeItem : BaseEntity
	{
		public uint ItemId { get; set; }

		[ForeignKey(nameof(ItemId))]
		public virtual Item Item { get; set; } = null!;

		public uint RecipeId { get; set; }

		[ForeignKey(nameof(RecipeId))]
		public virtual Recipe Recipe { get; set; } = null!;

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
