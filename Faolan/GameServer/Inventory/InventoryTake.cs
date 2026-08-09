using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;

namespace Faolan.GameServer.Inventory
{
	// Which slots a "take N of item X out of the inventory" touches, and by how much (#521's quest hand-in). PURE: the
	// arithmetic is separated from the DB write + wire push so it can be tested directly, the same split
	// QuestProgress.CanHandIn has from the service that supplies its count.
	//
	// The two things worth getting right, and neither is visible from a single-stack example:
	//   - a stack LARGER than what is wanted is decremented, not cleared - handing in 3 of 5 bricks must leave 2;
	//   - a stack can be SPLIT across cells and across containers, so the walk spans both and stops the moment the
	//     count is met rather than draining every slot holding the item.
	public static class InventoryTake
	{
		// One slot's share of the take. `Taken` is how many come out of it; `Remaining` is what is left behind, so 0
		// means the cell is cleared and anything else means it is rewritten with the smaller stack.
		//
		// A partial take DECREMENTS the item in place (the instance is not re-minted), so nothing per-instance needs
		// carrying through the step - the crafter and any socketed gems stay on the row by construction. The step used
		// to carry the CreatorName for exactly that reason: the cell was REWRITTEN, and the writer assigned the crafter
		// rather than leaving it (#727), so a decremented stack of a player-made item lost its "Created by" line
		// unless the plan passed the name back in. That whole class of hand-carrying is what #750 removed.
		public readonly record struct Step(ContainerKind Container, int Slot, uint Taken, uint Remaining);

		// The steps that remove `count` of `itemTemplateId`, in container-then-slot order. Takes as much as it can and
		// stops: a short plan (its Taken summing to less than `count`) means the player does not hold enough, which is
		// the caller's to report - this never throws and never over-takes.
		public static List<Step> Plan(IEnumerable<ItemInstance> items, uint itemTemplateId, uint count)
		{
			var steps = new List<Step>();
			if (count == 0 || itemTemplateId == 0)
				return steps;

			var remaining = count;
			foreach (var item in items.Where(i => i.TemplateId == itemTemplateId).OrderBy(i => i.Container).ThenBy(i => i.Slot))
			{
				if (remaining == 0)
					break;

				// An item with no explicit stack size is one item - the same reading HoldsDeliveryItem sums with, so the
				// check and the take can never disagree about how much a cell is worth.
				var held = item.Count == 0 ? 1u : item.Count;
				var taken = held < remaining ? held : remaining;
				remaining -= taken;
				steps.Add(new Step(item.Container, item.Slot, taken, held - taken));
			}

			return steps;
		}

		// How many the plan actually accounts for. Below the requested count, the inventory did not hold enough.
		public static uint Total(IEnumerable<Step> steps) => steps.Aggregate(0u, (sum, s) => sum + s.Taken);
	}
}
