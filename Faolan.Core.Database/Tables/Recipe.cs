using System.Collections.Generic;

namespace Faolan.Core.Database.Tables
{
	// A crafting/tradeskill recipe, as served in the TradeskillReplyIIR_c (0x9062D559) recipe-book list the client
	// requests when it opens the Crafting -> Recipes window (TradeskillRequestIIR_c 0x06B9CD94). Decoded from
	// NecroL80_mage-necromancer_shops_open-crrafting-receipies (frame 2926, 603 recipes) - the list is a 0x3F1-framed
	// vector of ClientTradeskillRecipe_c records; grammar + reader RE in docs/subsystems/tradeskill-recipes.md
	// (client readers FUN_007aab30 -> FUN_007abb30 -> FUN_0077be70). Keyed by the record's list-key (Id, which is
	// also repeated as header word 0 == the RequestRecipeInfo levelId).
	//
	// FULLY COLUMNIZED (#193): every wire word rebuilds from typed fields - the per-recipe header/mid/tail columns
	// below, the opaque ref-id list, and the ingredient/result elements (Items child rows + each item's Items-row TEA
	// key + hash). No verbatim blob. TradeskillRecipeCodec.ToRecord projects a row back to the wire record and
	// BuildReplyBody serialises it byte-for-byte (gated by TradeskillRecipeCodecTests).
	public class Recipe : BaseEntity
	{
		// The decoded ingredient (input) + result (output) items - the recipe's ingredient/result element lists
		// (TradeskillRecipeCodec elements). Each references an item template whose TEA key + hash supply the rest of
		// the wire element, so those are not duplicated here.
		public virtual ICollection<RecipeItem> Items { get; set; } = [];

		// The record's trailing flag byte (@0x5c): 0 or 1.
		public byte TrailingFlag { get; set; }

		// Per-recipe header word 1 (record header is [id][header1][rdbType]; word 0 == Id).
		public uint Header1 { get; set; }

		// The mid-header variable word (0 or 1); the surrounding mid words are fixed (0, 0x1F4, _, 0).
		public uint MidFlag { get; set; }

		// The tradeskill RDB type of the record: 0xf4241 (1000001) or 0xf4242 (1000002).
		public uint RdbType { get; set; }

		// The record's ref-id list (KEY2), 1 or 2 entries in every captured record. It is **the recipe's FEAT
		// REQUIREMENTS**, not an opaque list: every one of the 18 distinct ids resolves in `FeatIdMaps.WireFeatId`,
		// and through its `TemplateId` to a named tradeskill feat.
		//
		// `RequiredFeatId` is the crafting-skill feat that unlocks the recipe; `RequiredWorkshopFeatId`, present only
		// on the higher ranks, is the WORKSHOP the recipe additionally needs - and a workshop is a **guild-city
		// building**, not a personal feat. The feat table makes that plain: the same family holds
		// `Battlekeep Armorsmith Workshop I/II/III`, `Battlekeep Barracks`, `Architect Workshop` and
		// `Alchemist Workshop`. So a high-rank recipe is gated on the player's guild having built the thing.
		// The whole shipped set:
		//
		//     321000/321001/321100  Weaponsmith Novice / Apprentice / Adept        -            9/18/18 recipes
		//     321101/321200/321201  Weaponsmith Artisan / Expert / Master          Workshop I/II/III   18 each
		//     321300/321301/321400  Armorsmith  Novice / Apprentice / Adept        -           44/44/104
		//     321401/321500/321501  Armorsmith  Artisan / Expert / Master          Workshop I/II/III  104 each
		//
		// So the rule the data states is: from ARTISAN upward a recipe also requires a workshop, one tier per rank.
		// ⚠️ Only Weaponsmith and Armorsmith are present - the other tradeskills are simply not in the captured
		// records, not evidence that they work differently.
		//
		// ⚠️ Nothing gates on this yet - crafting checks neither feat, so every recipe is craftable by anyone.
		public uint RequiredFeatId { get; set; }

		// The record's trailing u32 (@0x50) before the flag byte.
		public uint Tail { get; set; }
		public uint? RequiredWorkshopFeatId { get; set; }
	}
}
