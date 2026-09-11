using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;

namespace Faolan.GameServer.Inventory
{
	// Which slots a "take N of item X out of the inventory" touches, and by how much - the quest hand-in's arithmetic.
	// Pure: it is separated from the database write and the wire push so it can be tested directly, the same split
	// QuestProgress.CanHandIn has from the service that supplies its count.
	//
	// The two things worth getting right, neither of which is visible from a single-stack example:
	//   - a stack larger than what is wanted is decremented rather than cleared - handing in 3 of 5 bricks leaves 2;
	//   - a stack can be split across cells and across containers, so the walk spans both and stops the moment the
	//     count is met rather than draining every slot holding the item.
	public static class InventoryTake
	{
		// One slot's share of the take. `Taken` is how many come out of it; `Remaining` is what is left behind, so 0
		// means the cell is cleared and anything else means it is rewritten with the smaller stack.
		//
		// A partial take decrements the item in place - the instance is not re-minted - so nothing per-instance needs
		// carrying through the step: the crafter and any socketed gems stay on the row by construction. Rewriting the
		// cell instead is what would make a decremented stack of a player-made item lose its "Created by" line.
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
