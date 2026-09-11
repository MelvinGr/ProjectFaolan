using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One ingredient (input) or result (output) item of a crafting Recipe - the recipe record's KEY1 (ingredients) /
	// listC (results) element lists (TradeskillRecipeCodec elements, see docs/subsystems/tradeskill-recipes.md). These
	// rows are the wire elements (the recipe rebuilds byte-exact from them via TradeskillRecipeCodec.ToRecord) and are
	// also queryable ("which recipes produce / consume item X"). Each references the item template by id (hard FK to
	// Items) - the item's TEA key (the same key RequestRecipeInfo 0x2090 uses) and hash live there, harvested into
	// Items from the recipe element by the recipes importer, so they're not duplicated here.
	// Keyed by its position in the recipe's element list: (recipe, which list, index in it). That is the wire
	// element's identity - the codec rebuilds the record by walking the two lists in Position order - so a
	// re-harvest reproduces the same keys instead of renumbering a surrogate.
	[PrimaryKey(nameof(RecipeId), nameof(Role), nameof(Position))]
	public class RecipeItem
	{
		public uint ItemId { get; set; }

		[ForeignKey(nameof(ItemId))]
		public virtual Item Item { get; set; } = null!;

		public uint RecipeId { get; set; }

		[ForeignKey(nameof(RecipeId))]
		public virtual Recipe Recipe { get; set; } = null!;

		[ConstrainToEnumValues]
		public RecipeItemRole Role { get; set; } // ingredient (input) or result (output)

		public int Position { get; set; } // order within its list

		public uint Flag { get; set; } // element field [3] (small enum-ish; 0x14/0x28/...)

		public uint Trailing { get; set; } // ingredient trailing word [17] (0 for results)
	}

	public enum RecipeItemRole : byte
	{
		Ingredient = 0,
		Result = 1
	}
}
