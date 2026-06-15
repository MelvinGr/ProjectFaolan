namespace Faolan.Core.Enums
{
	// What happens when a player USES (right-clicks) an item from a bag. The item-use dispatch (ItemUseService)
	// switches on this, so item identity lives in one classifier (ItemUseClassifier) rather than as name/id checks
	// scattered through the handler. Add a member as each on-use effect is wired.
	public enum ItemUseType
	{
		None = 0, // not a usable item (no on-use action wired)
		TotemRecall, // Totem of Origin -> recall to the bound resurrection point (#206)
		// HealthPotion, ManaPotion, Food, XpBoost, ... land here as the on-use effect is decoded/wired.
	}
}
