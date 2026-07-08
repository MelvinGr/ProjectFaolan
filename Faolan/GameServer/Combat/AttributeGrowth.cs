using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Text;

namespace Faolan.GameServer.Combat
{
	// Provisional per-level primary-attribute growth, the values fed to the AttributeChanged level-up feedback (#209).
	// Captures show every class raises all five primary attributes on a level-up (the same 5 category-615 ids appear
	// in every burst); only the per-attribute amount differs by class/level. The real per-class growth tables aren't
	// in the captures or DB, so - like LevelingResolver's linear XP ramp - this is a flat provisional model (+1 to
	// each primary per level gained) with the right SHAPE so the live feedback fires; swap in real tables later.
	public static class AttributeGrowth
	{
		// The attribute increases for advancing `levelsGained` levels (as `class`). Returns one (attributeId, amount)
		// per primary that rose; the caller emits one AttributeChanged message each. `class`/`newLevel` are accepted
		// for the eventual per-class/per-level table - the provisional model ignores them beyond the level count.
		public static IReadOnlyList<(uint AttributeId, uint Amount)> ForLevelGain(Class @class, uint newLevel, uint levelsGained = 1)
		{
			var amount = levelsGained == 0
				? 1u
				: levelsGained;
			var gains = new List<(uint, uint)>(AttributeChangedFeedback.PrimaryAttributeIds.Length);
			foreach (var id in AttributeChangedFeedback.PrimaryAttributeIds)
				gains.Add((id, amount));
			return gains;
		}
	}
}
