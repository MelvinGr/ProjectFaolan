using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// #720: an item ENTERING a player's inventory must pass through `GrantItemToBackpack`, because that is where
	// `AdvanceCollectGoals` fires. A path that MINTS an item itself delivers it fine and silently fails every collect
	// objective asking for it.
	//
	// The gate watches `SetCharacterItemSlot` - the one repository method that brings an item into existence. Its two
	// siblings are exempt BY CONSTRUCTION rather than by classification, which is the sharpest the census has been:
	// `MoveCharacterItem` and `SetCharacterItemCount` only ever act on a row that is already there, so neither can
	// introduce an item and neither can newly tick a collect goal. Before #750 there was no such distinction to draw -
	// a "move" was a pair of `SetCharacterInventorySlot` calls, indistinguishable from a grant, so an in-inventory move
	// had to be classified as "not a bypass, trust me" and EquipmentService accounted for 7 of the census' entries.
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
			["Faolan/GameServer/Economy/EconomyOutbound.cs"] =
				(3, "THE choke point itself (GrantItemToBackpack) + TakeItemsFromInventory's per-step clear + "
				    + "RemoveItemFromBackpack's bag clear (a clear cannot introduce an item, so it cannot tick a collect "
				    + "goal). The only file allowed to write a GRANT. It was GameServerListener.Outbound.cs until #826 moved "
				    + "the economy wire off the listener; the choke point moved with it, the rule did not, and the removal "
				    + "followed in #928 item 5."),

			["Faolan/GameServer/Inventory/EquipmentService.cs"] =
				(2, "a drop (clearing the dropped cell) and the new half of a stack SPLIT. Splitting is the one inventory "
				    + "action that legitimately mints, and it cannot tick a collect goal: the items were already held, the "
				    + "stack just became two. Equip/unequip/move are MoveCharacterItem calls and are structurally exempt."),

			["Faolan/GameServer/CreateFlow/CharacterCreationService.cs"] =
				(1, "the #220 STARTER GEAR, worn onto a brand-new character at creation. No collect goal can exist yet - "
				    + "the character has no quests - and the connection is still the character editor."),

			["Faolan/GameServer/Economy/ShopService.cs"] =
				(2, "a vendor SALE: clearing the sold slot, and restoring the partial stack when only part of it sold. "
				    + "The buy direction goes through GrantItemToBackpack."),

			["Faolan/GameServer/Economy/TradepostService.cs"] =
				(2, "ESCROW only - moving a listed item out of the seller's BANK on sell, and out of the sender's bank "
				    + "once a mail is taken. The take itself routes through GrantItemToBackpack (#720)."),

			// ⚠️ TradeService is deliberately ABSENT: it mints nothing and clears nothing. A trade HANDS the item over
			// (ITradeOutbound.TransferItemToCharacter -> the same choke point, moving the instance instead of minting), so
			// there is no giver-side clear to write either. It used to have two calls - a grant that skipped the choke
			// point (the #720 defect in the other delivery path, fixed by #726) and the giver's clear, which #750 removed
			// with the re-mint it belonged to.
		};

		[Fact]
		public void EveryInventoryRowWriter_IsClassified()
		{
			var found = new Dictionary<string, int>();
			foreach (var file in Directory.EnumerateFiles(TestPaths.Repo("Faolan"), "*.cs", SearchOption.AllDirectories))
			{
				if (file.Contains("/obj/") || file.Contains("/bin/"))
					continue;

				var calls = Regex.Matches(File.ReadAllText(file), @"SetCharacterItemSlot\s*\(").Count;
				if (calls == 0)
					continue;

				var rel = Path.GetRelativePath(TestPaths.Repo(), file).Replace('\\', '/');
				found[rel] = calls;
			}

			var unclassified = found.Keys.Where(f => !Writers.ContainsKey(f)).OrderBy(f => f).ToList();
			Assert.True(unclassified.Count == 0,
				"A new path MINTS item instances directly. If it DELIVERS an item to a player it must go "
				+ "through GrantItemToBackpack instead, or collect objectives will silently not tick (#720). If it is a "
				+ "removal or a split, classify it in InventoryGrantChokePointTests.Writers:\n  "
				+ string.Join("\n  ", unclassified));

			var changed = Writers
				.Where(w => found.TryGetValue(w.Key, out var n) && n != w.Value.Calls)
				.Select(w => $"{w.Key}: {found[w.Key]} calls, classified as {w.Value.Calls} - {w.Value.Why}")
				.ToList();

			Assert.True(changed.Count == 0,
				"A classified file gained or lost an item MINT. Confirm the new call is not a GRANT "
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
			Assert.DoesNotContain("SetCharacterItemSlot(character.Id", take);
		}
	}
}
