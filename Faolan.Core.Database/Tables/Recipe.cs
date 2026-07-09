using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// A crafting/tradeskill recipe, as served in the TradeskillReplyIIR_c (0x9062D559) recipe-book list the client
	// requests when it opens the Crafting -> Recipes window (TradeskillRequestIIR_c 0x06B9CD94). Decoded from
	// NecroL80_mage-necromancer_shops_open-crrafting-receipies (frame 2926, 603 recipes) - the list is a 0x3F1-framed
	// vector of ClientTradeskillRecipe_c records; grammar + reader RE in docs/tradeskill-recipes.md
	// (client readers FUN_007aab30 -> FUN_007abb30 -> FUN_0077be70). Keyed by the record's list-key (the recipe id
	// the loop maps by; == the RequestRecipeInfo levelId).
	//
	// Each record carries the produced item + its input ingredients as item template ids WITH their per-item TEA
	// DecryptionKeys (the same keys RequestRecipeInfo 0x2090 later references), plus two 0x3F1-framed sub-lists
	// (ingredients / results). Those inner fields are not yet split into their own columns - the whole record body
	// is carried VERBATIM in Data so the book is reproduced byte-for-byte (a decoded-framing verbatim interim, like
	// the item templates); normalising ingredients/results into child tables is the follow-up.
	public class Recipe : BaseEntity
	{
		// The decoded ingredient (input) + result (output) items - a queryable projection of Data's KEY1/listC
		// element lists (TradeskillRecipeCodec.ParseRecordItems). Data stays the byte-exact wire source.
		public virtual ICollection<RecipeItem> Items { get; set; } = new List<RecipeItem>();

		// The record bytes AFTER the leading list-key (which is Id): everything FUN_0077be70 reads plus the trailing
		// 1-byte flag. The wire list element is [u32 Id][Data]; the builder reproduces exactly that.
		[Column(TypeName = "BLOB")]
		public byte[] Data { get; set; } = [];
	}
}
