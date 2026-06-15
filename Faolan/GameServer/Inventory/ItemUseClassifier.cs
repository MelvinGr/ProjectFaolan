using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;

namespace Faolan.GameServer.Inventory
{
	// Classifies an item TEMPLATE to what using it does (ItemUseType), so ItemUseService switches on a stable
	// category instead of comparing names. Item identity lives HERE, keyed by the stable template id - NOT a name
	// compare (rename/locale fragile) and NOT a per-id switch in the service.
	//
	// Today this is a small explicit map. The scalable, data-driven source is the item's RDB ON-USE behavior (the
	// on-use spell - heal/mana/xp for potions), which isn't decoded yet (same family as the stat-attribute decode,
	// docs/item-template-stats.md). Bulk consumables (potions/food) should be classified by that effect once decoded
	// - keyed off a derived column or the SearchCategory - rather than by enumerating hundreds of template ids here.
	public static class ItemUseClassifier
	{
		// Totem of Origins template (faolan.db). Recall item; future totem tiers add their ids to this case.
		private const uint TotemOfOriginsId = 3514741;

		public static ItemUseType Classify(Item item)
		{
			if (item == null)
				return ItemUseType.None;

			return item.Id switch
			{
				TotemOfOriginsId => ItemUseType.TotemRecall,
				_ => ItemUseType.None
			};
		}
	}
}
