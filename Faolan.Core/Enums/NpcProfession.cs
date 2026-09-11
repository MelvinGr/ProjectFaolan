namespace Faolan.Core.Enums
{
	// The role an NPC plays for the player, from the client's own spawn record: `ProtoCoreCharacterIIR.NpcStats`
	// field 12, `profession_type` (`NpcStatCore.ProfessionType`). It is what makes "which NPCs are auctioneers"
	// answerable without guessing from display names.
	//
	// Deliberately not `[ConstrainToEnumValues]`, even though every value the shipped world carries is named. The
	// column is open: the world is pruned, so a profession no imported template happens to play is a real client
	// value we have simply not met, not a corrupt row. Constraining it would reject valid data on the next harvest.
	//
	// Every member is named from the client's own string, not from what its templates look like. The sibling column
	// `ProfessionName` is an id into LDB category 253, the profession / shop-kind table - usually the ELF hash of the
	// string (`elf("Merchant") == 210363236`, `elf("Trainer") == 193463442`), and sometimes a small sequential id
	// (1, 2, 22, 28, 44, 46, 53, 56), so the column is not uniformly hashed. All 88 distinct values in the shipped
	// data resolve there; `--report professions` prints the whole catalog.
	//
	// `ProfessionName` is finer-grained than the type - 58 of its values sit inside `Merchant` alone ("Armorsmith",
	// "Master Potions", "Aquilonian Dyes", "Veteran Rewards", ...) - so the type is the role and the name is the shop
	// kind. Where a type has exactly one name, that name is the role and is what the member below is called.
	//
	// The resolved string is deliberately not stored on `NpcStatCore`. LDB pages are per-language while the id is
	// not, so persisting the English text would bake a language into the seed; the id resolves at display time.
	public enum NpcProfession
	{
		// The auction house operator - what retail calls a "Trader" and what opens the Tradepost window on a click.
		//
		// Exclusive: all four templates carrying it are named "Trader" (8554, 8675, 11768, 14712), and no template
		// with any other profession is. Matching the display name "Trader" instead could not tell an auctioneer from
		// any other NPC that happened to share the name.
		Auctioneer = 1,

		// A shop vendor. The largest group by far (112 templates, 58 distinct ProfessionNames - the sub-kind lives in
		// that column, not this one). Confirmed by name rather than inferred: `elf("Merchant")` is exactly the
		// ProfessionName the biggest of those groups carries.
		Merchant = 3,

		// A local travel operator - the wagoneers, caravan masters and smugglers who move the player for a fee.
		// 19 templates: every "Wagoner <name>" in the world plus Tortage Smuggler, Smuggler, Perlatus and Recruiter
		// Cevarius. Note the travel runtime does not key on this - a TravelDestination row is a dialogue branch - so
		// this is identification, not routing.
		Traveller = 6,

		// The black marketeer. 10 templates, all named "Black Marketeer". Their ProfessionName is a shop kind rather
		// than the role ("Fine Potions", "Common Food and Drinks", ...), the same shape as a Merchant's - the role is
		// carried by this column alone.
		BlackMarketeer = 8,

		// A city/settlement guard. 27 templates - the Guards, Guardsmen, Sentinels and City Guards.
		Guard = 9,

		// The client's own name for it, verbatim: LDB[253] "Wayfarer of Asura", carried by all five templates of this
		// type (Blaine, Tanor, Malia, Nidd, Finian) and by nothing else. Named from the string rather than from a guess
		// at what the NPC does - the five display names give no hint, and inventing a functional label ("respec",
		// "resurrection") would be a claim the data does not make.
		WayfarerOfAsura = 2

		// Type 4 is deliberately not named. Its one labelled template (Fenor) says LDB[253] "Redeemer", but the
		// type has nine templates and the other eight - Farmer Livinius, Amobia, Vialli, four Peasant Ghosts, Scipio -
		// carry no ProfessionName at all. Naming a type from 1 of 9 rows would describe Fenor, not the type. Type 4
		// needs a capture that labels one of the other eight, or the client's own profession-type table.
		//
		// `NpcProfessionTests.EveryTemplateOfANamedProfession_CarriesAProfessionName` keeps this rule: a member may
		// only be named once every template of its type is labelled.
	}
}
