using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// #720: an item ENTERING a player's inventory must pass through `GrantItemToBackpack`, because that is where
	// `AdvanceCollectGoals` fires. A path that writes `SetCharacterInventorySlot` itself delivers the item fine and
	// silently fails every collect objective asking for it.
	//
	// That is not hypothetical - it shipped twice. `TradepostService.TakeMailItem` found its own bag slot, built its
	// own wire entry and wrote its own row, so an item bought from the auction house and taken from mail did not
	// advance a collect goal while the same item looted, bought from a vendor, harvested, claimed or `.additem`ed
	// did. It was found by INSPECTION, not by a gate. This is the gate.
	//
	// It is deliberately coarse - a per-file census rather than a per-call analysis - because the thing worth
	// catching is "a NEW path started writing inventory rows", and that shows up as a new file or a new call in an
	// existing one. Every entry below is classified; adding a call means saying which kind it is.
	public class InventoryGrantChokePointTests
	{
		// file -> (expected call count, why those calls are not a bypass)
		private static readonly Dictionary<string, (int Calls, string Why)> Writers = new()
		{
			["Faolan/GameServer/GameServerListener.Outbound.cs"] =
				(4, "THE choke point itself (GrantItemToBackpack) + removals: a bag clear and TakeItemsFromInventory's "
				    + "per-step clear/partial-stack restore. The only file allowed to write a GRANT."),

			["Faolan/GameServer/Inventory/EquipmentService.cs"] =
				(7, "MOVES within the player's own inventory - equip/unequip, slot swap, stack split, bag re-place. The "
				    + "item was already held, so nothing is entering the inventory and no collect goal can newly tick."),

			["Faolan/GameServer/Economy/ShopService.cs"] =
				(2, "a vendor SALE: clearing the sold slot, and restoring the partial stack when only part of it sold. "
				    + "The buy direction goes through GrantItemToBackpack."),

			["Faolan/GameServer/Economy/TradepostService.cs"] =
				(2, "ESCROW only - moving a listed item out of the seller's BANK on sell, and out of the sender's bank "
				    + "once a mail is taken. The take itself routes through GrantItemToBackpack (#720)."),

			// ⚠️ A KNOWN BYPASS, tracked - see the test below.
			["Faolan/GameServer/Economy/TradeService.cs"] =
				(2, "player-to-player trade: clears the giver's slot and writes the receiver's DIRECTLY. The second of "
				    + "those IS a grant that skips the choke point - the same defect as #720, tracked separately.")
		};

		[Fact]
		public void EveryInventoryRowWriter_IsClassified()
		{
			var found = new Dictionary<string, int>();
			foreach (var file in Directory.EnumerateFiles(TestPaths.Repo("Faolan"), "*.cs", SearchOption.AllDirectories))
			{
				if (file.Contains("/obj/") || file.Contains("/bin/"))
					continue;

				var calls = Regex.Matches(File.ReadAllText(file), @"SetCharacterInventorySlot\s*\(").Count;
				if (calls == 0)
					continue;

				var rel = Path.GetRelativePath(TestPaths.Repo(), file).Replace('\\', '/');
				found[rel] = calls;
			}

			var unclassified = found.Keys.Where(f => !Writers.ContainsKey(f)).OrderBy(f => f).ToList();
			Assert.True(unclassified.Count == 0,
				"A new path writes CharacterInventory rows directly. If it DELIVERS an item to a player it must go "
				+ "through GrantItemToBackpack instead, or collect objectives will silently not tick (#720). If it is a "
				+ "removal or an in-inventory move, classify it in InventoryGrantChokePointTests.Writers:\n  "
				+ string.Join("\n  ", unclassified));

			var changed = Writers
				.Where(w => found.TryGetValue(w.Key, out var n) && n != w.Value.Calls)
				.Select(w => $"{w.Key}: {found[w.Key]} calls, classified as {w.Value.Calls} - {w.Value.Why}")
				.ToList();

			Assert.True(changed.Count == 0,
				"A classified file gained or lost a CharacterInventory write. Confirm the new call is not a GRANT "
				+ "bypassing GrantItemToBackpack, then update the count:\n  " + string.Join("\n  ", changed));

			var gone = Writers.Keys.Where(f => !found.ContainsKey(f)).OrderBy(f => f).ToList();
			Assert.True(gone.Count == 0, "Classified writers that no longer write - drop them from the list:\n  " + string.Join("\n  ", gone));
		}

		// The mail take specifically: it must NOT rebuild the delivery itself. Pinned because that is exactly what
		// #720 was - the private slot search + wire builder + row write sitting beside the choke point rather than
		// inside it - and it read as deliberate, since it carried a comment explaining why it was special.
		[Fact]
		public void TakeMailItem_DeliversThroughTheGrant()
		{
			var src = File.ReadAllText(TestPaths.Repo("Faolan", "GameServer", "Economy", "TradepostService.cs"));
			var take = src[src.IndexOf("private async Task TakeMailItem", System.StringComparison.Ordinal)..];

			// Bound at the next member, or the end of the class when it is the last one - which it now is, since
			// routing through the grant deleted the private slot-finder and wire builder that used to follow it.
			var next = take.IndexOf("\n\t\tprivate ", System.StringComparison.Ordinal);
			if (next > 0)
				take = take[..next];

			Assert.Contains("GrantItemToBackpack", take);
			Assert.DoesNotContain("SetCharacterInventorySlot(character.Id", take);
		}
	}
}
