namespace Faolan.Core.Enums
{
	// Broad classification of an item template. The values ARE the protocol inventory-container kinds the item
	// lives in (FullCharSnapshotBuilder / ProtoInventoryGame inventory_id.type), so the enum doubles as a routing
	// hint: Normal items go in the backpack (104; also worn in equipped 101), Resource/tradeskill items in the
	// resource container (112), Quest items in the quest container (113). Harvested from captures by the container
	// an item was seen in (ArchetypeHarvester); items never seen in a quest/resource container default to Normal.
	// Used to keep quest/resource items out of random loot (GetRandomBuildableItems).
	public enum ItemType
	{
		// No Equipped(101): the equipped container is a LOCATION, not an item class - worn gear is Normal loot, so the
		// harvester (ArchetypeHarvester.ClassifyContainer) folds container-kind 101 into Normal.
		Normal = 104, // BagKind
		Resource = 112, // ResourceKind
		Quest = 113 // QuestKind
	}
}
