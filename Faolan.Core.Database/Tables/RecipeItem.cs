using Faolan.Core.Database.Attributes;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Database.Tables
{
	// One ingredient (input) or result (output) item of a crafting Recipe - a queryable projection of the recipe
	// record's KEY1 (ingredients) / listC (results) element lists (decoded in TradeskillRecipeCodec.ParseRecordItems,
	// see docs/subsystems/tradeskill-recipes.md). The recipe still ships byte-exact from Recipe.Data; these rows exist so
	// recipes are queryable ("which recipes produce / consume item X"). Each carries the referenced item template
	// id + its 16-byte TEA DecryptionKey (Data1..4) - the same key RequestRecipeInfo (0x2090) uses to fetch the
	// item's details.
	public class RecipeItem : BaseEntity
	{
		public uint ItemId { get; set; } // the referenced item template id

		public uint RecipeId { get; set; } // FK -> Recipe.Id
		public virtual Recipe Recipe { get; set; }

		public int Position { get; set; } // order within its list

		[ConstrainToEnumValues]
		public RecipeItemRole Role { get; set; } // ingredient (input) or result (output)

		// The referenced item's 128-bit TEA DecryptionKey (element words [5..8]; owned -> flat columns Key1..Key4).
		// virtual: see Item.Key. Same key RequestRecipeInfo (0x2090) uses to fetch the item's details.
		public virtual ProtoDecryptionKey Key { get; set; } = new();

		public uint Flag { get; set; } // element field [3] (small enum-ish; 0x14/0x28/...)
		public uint Hash { get; set; } // element field [4]

		// TODO: Decode
		public uint Trailing { get; set; } // ingredient trailing word [17] (0 for results)
	}

	public enum RecipeItemRole : byte
	{
		Ingredient = 0,
		Result = 1
	}
}
