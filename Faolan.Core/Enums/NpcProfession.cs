namespace Faolan.Core.Enums
{
	// The ROLE an NPC plays for the player, from the client's own spawn record: `ProtoCoreCharacterIIR.NpcStats`
	// field 12, `profession_type` (`NpcStatCore.ProfessionType`). It is what makes "which NPCs are auctioneers"
	// answerable without guessing from display names (Gitea #647).
	//
	// ⚠️ **A PARTIAL catalog, and deliberately not `[ConstrainToEnumValues]`.** The column is OPEN: eight distinct
	// values occur across the 1,018 imported templates and the members below are the ones the shipped data actually
	// evidences. An unnamed value is a real profession we have not identified, not a corrupt row, so constraining the
	// column would reject valid client data.
	//
	// Each member's evidence is the set of templates that carry it, which is checked by
	// `NpcProfessionTests` rather than asserted here. The sibling column `ProfessionName` is the ELF hash of the
	// profession's name string - `elf("Merchant") == 210363236` and `elf("Trainer") == 193463442` both land exactly,
	// which is what identifies the encoding - but it is finer-grained than the type (88 distinct values, 60 of them
	// inside type 3 alone) and is a per-shop-kind label rather than the role. Naming the rest of it is Gitea #649.
	public enum NpcProfession
	{
		// The AUCTION HOUSE operator - what retail calls a "Trader" and what opens the Tradepost window on a click.
		//
		// Exclusive: all four templates carrying it are named "Trader" (8554, 8675, 11768, 14712), and no template
		// with any other profession is. This is what replaced matching the display name "Trader", which could not tell
		// an auctioneer from any other NPC that happened to share the name and pinned only the one template the
		// corpus captured.
		Auctioneer = 1,

		// A shop VENDOR. The largest group by far (112 templates, 58 distinct ProfessionNames - the sub-kind lives in
		// that column, not this one). Confirmed by name rather than inferred: `elf("Merchant")` is exactly the
		// ProfessionName the biggest of those groups carries.
		Merchant = 3,

		// A LOCAL TRAVEL operator - the wagoneers, caravan masters and smugglers who move the player for a fee (#514).
		// 19 templates: every "Wagoner <name>" in the world plus Tortage Smuggler, Smuggler, Perlatus and Recruiter
		// Cevarius. Note the travel runtime does NOT key on this - a TravelDestination row IS a dialogue branch - so
		// this is identification, not routing.
		Traveller = 6,

		// The BLACK MARKETEER. 10 templates, all named "Black Marketeer".
		BlackMarketeer = 8,

		// A city/settlement GUARD. 27 templates - the Guards, Guardsmen, Sentinels and City Guards.
		Guard = 9
	}
}
