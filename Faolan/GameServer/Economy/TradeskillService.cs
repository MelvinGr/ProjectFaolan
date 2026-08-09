using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Economy;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Economy
{
	// Crafting (#732's sibling): turning a recipe's ingredients into its product. The recipe BOOK already shipped -
	// TradeskillRequestIIR -> TradeskillReplyIIR - but nothing consumed materials or produced an item.
	//
	// The whole transaction is captured in NecroL80_mage-necromancer_login_gameplay_annotated_5, and it is worth reading
	// as one sequence because every step below is one of its lines. Melvin linked the item in /say before and after
	// and narrated "done" in between, which is how the craft is locatable at all.
	//
	//   18:45:25 c2s BehaviorControlCenter  BehaviorId.Tradeskill (0x27), tail [flag 1][recipe 15][count 1]
	//   18:45:26 s2c   the behavior ack
	//   18:45:30 s2c   DispatchSetItemStat x3   the destination cell's StaticInstance/Duration bookkeeping
	//                  DispatchAddItem          the product, carrying creator_name "NecroL80"
	//                  DispatchRemoveItem       bag 0x68 pos 19    - Plain Hilt Piece, consumed whole
	//                  DispatchRemoveItem       resource 0x70 pos 13 - Copper, consumed whole
	//                  DispatchSetItemStat      resource 0x70 pos 10 StackSize 2 - Silver, decremented
	//                  DispatchTradeskillComplete 0x0000000F
	//
	// The `0x0F` is the RECIPE ID, and recipe 15 in our own catalog is exactly Plain Hilt Piece x1 + Copper x4 +
	// Silver x4 -> Bloodsilver Khopesh. The removals corroborate the quantities to the unit: his copper stack ran 4 -> 0
	// and his silver stack 6 -> 2. So the shipped Recipes/RecipeItems rows ARE retail's, and nothing here is invented.
	// [V2: NecroL80_mage-necromancer_login_gameplay_annotated_5 fr17374/17376/17401/17402]
	//
	// The recipes are SERVER data by construction: they were harvested from the s2c TradeskillReplyIIR, i.e. retail
	// telling the client what its book holds, ingredients and product included. There is no client-side recipe table
	// to fall back on.
	//
	// ⚠️ Retail takes ~5s between the ack and the completion - a cast-like bar the client animates. We complete
	// immediately, which cuts the animation short but delivers the same items in the same order. Timing it needs a
	// scheduled completion, which is its own change.
	//
	// ⚠️ NOTHING GATES THIS ON A PROFESSION. In retail a character must LEARN a tradeskill profession, which comes in
	// ranks, and that is what gates the recipe book - a fresh character has no book at all. `SendCraftingRecipeBook`
	// hands the WHOLE 603-row `Recipes` table to any character whose client asks, and this method checks only the
	// catalog and the materials. So the only thing standing between a level-1 character and a master-tier recipe is
	// whether the client asks.
	//
	// The professions themselves ARE persisted and ARE sent correctly: they ride the spawn scaffold's feat/ability
	// list (`CharacterSkills`, the `[u8 rank][u32 id]` groups), imported per character from that character's own
	// capture and replayed verbatim - which is why an imported character's professions match retail exactly. What is
	// missing is the MEANING, not the data: nothing here knows which of those ids is a profession, what the rank byte
	// counts, or which recipes a given profession covers. The `1.4M` band is the standing candidate (DemoL80 carries 7
	// of them at rank 3, NecroL80 1), but it is not confirmed, so nothing is gated on it - a guessed rule would be worse
	// than an open book and would have to be unpicked. Tracked in Gitea.
	public sealed class TradeskillService
	{
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;
		private readonly IEconomyOutbound _outbound;

		public TradeskillService(IDatabaseRepository database, IEconomyOutbound outbound, ILogger logger)
		{
			_database = database;
			_outbound = outbound;
			_logger = logger;
		}

		// Puts a GEM into a socket of another item (#754) - the `Tradeskill` command's second operation.
		//
		// Returns true when a gem was actually socketed. A refusal is SILENT, like a refused craft: retail answers a
		// successful tradeskill and says nothing otherwise, so telling the client about a rejection would be
		// inventing a message.
		//
		// ⚠️ Socketing CONSUMES the gem, which is why every rule is checked before the write rather than trusting the
		// client to have only offered a legal combination. The client does validate - it draws the slots and their
		// colours - so these are the defence against a modified one.
		public async Task<bool> Socket(INetworkClient client, InventoryAddress target, InventoryAddress gem)
		{
			var character = client.Session.Character;
			if (target.Container != gem.Container || target.Slot == gem.Slot)
				return false; // both addresses are cells of the player's own bag in every captured socketing

			var host = await _database.GetCharacterItemSlot(character.Id, target.Container, (int)target.Slot);
			var stone = await _database.GetCharacterItemSlot(character.Id, gem.Container, (int)gem.Slot);
			if (host == null || stone == null)
			{
				_logger.LogInformation("Socket refused: {Character} named {Container}[{Target}] + [{Gem}] and one of them is empty.", character.Name, target.Container, target.Slot,
					gem.Slot);
				return false;
			}

			// How many sockets the item HAS - `NumGemSlots`, template field 114. Zero means it takes no gems at all,
			// and socketing into it would destroy the gem for nothing.
			var slots = host.Template.GemSlots;
			if (slots == 0)
			{
				_logger.LogInformation("Socket refused: {Item} has no gem sockets.", host.Template.Name);
				return false;
			}

			// The first FREE index. An item may already hold more gems than it declares - retail reduced some slot
			// counts in a later patch and the old items kept their gems, which the client itself calls GemOverflow -
			// so this bounds what may be ADDED and deliberately does not judge what is already there.
			var taken = host.Sockets.Select(x => x.SocketIndex).ToHashSet();
			var index = Enumerable.Range(0, (int)slots).Select(i => (uint)i).Cast<uint?>().FirstOrDefault(i => !taken.Contains(i!.Value));
			if (index == null)
			{
				_logger.LogInformation("Socket refused: {Item} has all {Slots} socket(s) filled ({Held} gem(s) held).", host.Template.Name, slots, host.Sockets.Count);
				return false;
			}

			var socket = await _database.SocketGem(host.Id, index.Value, stone.Id);
			if (socket == null)
				return false;

			await _outbound.SendGemSocketed(client, target.Container, target.Slot, socket, gem.Slot);
			_logger.LogInformation("Socketed {Gem} into {Item} slot {Index} for {Character}.", stone.Template.Name, host.Template.Name, index.Value, character.Name);

			return true;
		}

		// Runs one craft. Returns the recipe id to echo in DispatchTradeskillComplete, or null when nothing was made -
		// the caller stays silent then, because retail's completion means "it worked" and sending one anyway would tell
		// the client to close a window over an item it never received.
		public async Task<uint?> Craft(INetworkClient client, uint recipeId, uint count)
		{
			var character = client.Session.Character;
			if (count == 0)
				return null;

			var recipe = await _database.GetRecipe(recipeId);
			if (recipe == null)
			{
				_logger.LogInformation("Craft refused: {Character} asked for recipe {Recipe}, which is not in the catalog.", character.Name, recipeId);
				return null;
			}

			var product = recipe.Items.FirstOrDefault(i => i.Role == RecipeItemRole.Result);
			if (product == null)
			{
				// A recipe with no result is a harvest artefact, not a craft - refuse rather than consume the
				// ingredients for nothing.
				_logger.LogWarning("Craft refused: recipe {Recipe} has no result item.", recipeId);
				return null;
			}

			// Ingredients are summed by TEMPLATE first: a recipe listing the same item twice (or the same material
			// under two positions) must be checked and taken as one total, or a two-position entry passes a per-row
			// check while the second take comes up short.
			var needed = new Dictionary<uint, uint>();
			foreach (var ingredient in recipe.Items.Where(i => i.Role == RecipeItemRole.Ingredient))
			{
				// A recipe element's `Trailing` is its quantity; the captured rows use 0 only on the RESULT, so an
				// ingredient reading 0 means one, the same reading the inventory walk uses for a stackless cell.
				var quantity = ingredient.Trailing == 0 ? 1u : ingredient.Trailing;
				needed[ingredient.ItemId] = needed.GetValueOrDefault(ingredient.ItemId) + (quantity * count);
			}

			// CHECK EVERYTHING BEFORE TAKING ANYTHING. Taking as we go would leave a player who is one silver short
			// having paid the copper - the same grant-before-consume discipline the shop's buy path and the harvest
			// bag-full refusal use.
			var held = await _database.GetCharacterItems(character.Id);
			foreach (var (itemId, quantity) in needed)
			{
				var have = held.Where(i => i.TemplateId == itemId).Aggregate(0u, (sum, i) => sum + (i.Count == 0 ? 1u : i.Count));
				if (have >= quantity)
					continue;

				_logger.LogInformation("Craft refused: {Character} holds {Have} of item {Item} but recipe {Recipe} needs {Need}.", character.Name, have, itemId, recipeId, quantity);
				return null;
			}

			// The product first, so a full bag refuses the craft with the materials still in it.
			var productItem = await _database.GetItem(product.ItemId);
			var slot = await _outbound.GrantItemToBackpack(client, product.ItemId, count, creatorName: character.Name);
			if (slot == null)
			{
				_logger.LogInformation("Craft refused: {Character}'s bag is full, so recipe {Recipe} was not crafted (materials kept).", character.Name, recipeId);
				return null;
			}

			foreach (var (itemId, quantity) in needed)
			{
				var taken = await _outbound.TakeItemsFromInventory(client, itemId, quantity);
				if (taken < quantity)
				{
					// The pre-check passed, so a short take means the inventory moved underneath us. Worth a line: the
					// player has been undercharged for an item they already hold.
					_logger.LogWarning("Craft: took only {Taken} of {Need} item {Item} for {Character}'s recipe {Recipe} - inventory changed mid-craft.", taken, quantity, itemId, character.Name,
						recipeId);
				}
			}

			_logger.LogInformation("Craft: {Character} made {Count}x {Product} ({ProductId}) from recipe {Recipe}, into bag slot {Slot}.", character.Name, count, productItem?.Name ?? "?",
				product.ItemId, recipeId, slot);
			return recipeId;
		}
	}
}
