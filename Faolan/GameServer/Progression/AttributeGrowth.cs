using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Text;

namespace Faolan.GameServer.Progression
{
	// Provisional per-level primary-attribute growth, the values fed to the AttributeChanged level-up feedback.
	// Captures show every class raises all five primary attributes on a level-up - the same five category-615 ids
	// appear in every burst - and only the per-attribute amount differs by class and level. The real per-class growth
	// tables are in neither the captures nor the database, so this is a flat model (+1 to each primary per level
	// gained) with the right shape for the live feedback to fire, to be replaced by real tables.
	public static class AttributeGrowth
	{
		// The attribute increases for advancing `levelsGained` levels (as `class`). Returns one (attributeId, amount)
		// per primary that rose; the caller emits one AttributeChanged message each. `class` and `newLevel` are accepted
		// for the eventual per-class, per-level table - this model ignores them beyond the level count.
		public static IReadOnlyList<(uint AttributeId, uint Amount)> ForLevelGain(ConanClass @class, uint newLevel, uint levelsGained = 1)
		{
			var amount = levelsGained == 0
				? 1u
				: levelsGained;

			var gains = new List<(uint, uint)>(AttributeChangedCodec.PrimaryAttributeIds.Length);
			foreach (var id in AttributeChangedCodec.PrimaryAttributeIds)
				gains.Add((id, amount));

			return gains;
		}
	}
}
