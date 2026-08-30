using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Progression;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Economy
{
	// Crafting (#732's sibling): turning a recipe's ingredients into its product. The recipe BOOK already shipped -
	// TradeskillRequestIIR -> TradeskillReplyIIR - but nothing consumed materials or produced an item.
	//
	// The whole transaction is captured in NecroL80_mage-necromancer_login_gameplay_annotated_5, and it is worth reading
	// as one sequence because every step below is one of its lines. The maintainer linked the item in /say before and after
	// and narrated "done" in between, which is how the craft is locatable at all.
	//
	//   18:45:25 c2s BehaviorControlCenter  BehaviorId.Tradeskill (0x27), tail [flag 1][recipe 15][count 1]
	//   18:45:26 s2c   the behavior ack
	//   18:45:30 s2c   DispatchSetItemStat x3   the destination cell's StaticInstance/Duration bookkeeping
	//                  DispatchAddItem          the product, carrying the crafter's creator_name
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
	// ⭐ RETAIL PACES IT, and the pace is the client's own data (#793). A tradeskill is a COMMAND QUEUE command -
	// `CommandQueue\\tradeskillcommand.cpp`, `TradeskillCommand_c` - and its duration getter (`FUN_0114bf50`) answers per
	// operation: a CRAFT returns the recipe record's `+0x24`, which is the mid-header word
	// `TradeskillReplyCodec.CraftDurationCentiseconds` (`0x1F4` on every one of the 603 shipped recipes); a gem ADD
	// returns a hardcoded 500; a gem REMOVE 0; anything else 100. The unit is centiseconds, the same as the BCC's
	// `activation_time`, and it is what sizes the client's `TradeskillProgressBarDesc` bar.
	//
	// Both numbers are confirmed on the wire, SERVER-side, in two different captures - so the hold is real pacing and
	// not a client-only animation:
	//   craft  ..._annotated_5  fr17374 -> fr17402   18:45:25.441 -> 18:45:30.801, RTT/2 ~140 ms -> 5.07 s
	//   socket ..._annotated_8  fr8215  -> fr8248    06:52:55.276 -> 06:53:00.384, RTT/2  ~65 ms -> 4.98 s
	// [V2: NecroL80_mage-necromancer_login_gameplay_annotated_5 fr17374/17402; NecroL80_mage-necromancer_login_gameplay_annotated_8 fr8215/8248]
	//
	// So the inbound handler STAGES the transaction (`Stage`) and the world tick runs it once its deadline passes
	// (`CompleteDue`). Nothing is checked, taken or granted until then - which is also retail's order: the COMPLETION
	// decides, which is why a player who empties their own bag mid-bar is refused at the end of the bar, not its start.
	//
	// ⭐ THE PROFESSION GATE (#735). A character must have TRAINED a tradeskill to see or use a recipe, and the model
	// is the client's own: a profession is a strict linear chain of feats in `FeatPrereqs`, seven ranks deep, and
	// every one of the 603 recipes states the rung it needs in `RequiredFeatId`. `TradeskillLadders` walks the chains;
	// `BookFor` filters the book to what the character has reached and `HasProfessionFor` gates the craft itself.
	// A character who has trained nothing gets an EMPTY book, which is retail - the book is what a profession buys.
	//
	// ⚠️ The `1.4M` band in `CharacterSkills` is NOT the professions, though it was the standing candidate. Resolved
	// through `FeatIdMaps`, the eight ids the two imported characters carry are ordinary combat feats - `Soothing
	// Glamour`, `Precise Strikes`, `Field of War`, `Chromatic Warding`, `Innate Critigation Chance`. Falsified, so
	// nothing reads them.
	//
	// ⚠️ So no captured character carries a tradeskill feat at all, and yet the L80 above crafted a Weaponsmith
	// recipe - which says the profession does NOT ride the spawn scaffold. What DOES express it on the wire is the
	// book: the 603 rows we ship were harvested from that one character's `TradeskillReplyIIR`, which is why they are
	// Weaponsmith and Armorsmith and nothing else. Our "catalog" is one player's book. The retail LEARN path is
	// therefore still uncaptured, and `.tradeskill` is the GM bootstrap that stands in for it.
	public sealed class TradeskillService
	{
		// The client's hardcoded duration for putting a gem IN (`FUN_0114bf50` case 2), in centiseconds. Removing one
		// is 0 there - instant - which is the only reason there is no constant for it: we do not implement removal.
		public const uint GemSocketCentiseconds = 500;

		// GATHERING's wire side: a swung node restates its health to everyone looking at it, and an exhausted one
		// despawns. Both are ordinary world-object broadcasts - which is the whole of what this service needs from the
		// world, so it takes the object service rather than the broadcaster (#826).
		private readonly WorldObjectService _worldObjects;

		// The refusal lines a harvest can produce ("already harvested", "bag full", "not yet harvestable") - said by
		// the NODE, which is how retail words them, rather than as a system message.
		private readonly IChatServerDispatcher _chat;
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;
		private readonly IEconomyOutbound _outbound;

		// The staged tradeskills, oldest first. A plain list under a lock rather than a per-client slot: the client's
		// own command QUEUE can hold more than one, and dropping a second craft on the floor would silently eat it.
		private readonly List<StagedTradeskill> _staged = [];

		public TradeskillService(IDatabaseRepository database, IEconomyOutbound outbound, WorldObjectService worldObjects, IChatServerDispatcher chat, ILogger logger)
		{
			_database = database;
			_outbound = outbound;
			_worldObjects = worldObjects;
			_chat = chat;
			_logger = logger;
		}

		// One tradeskill waiting out its bar. `Request` is the parsed command verbatim, so the completion runs exactly
		// what was asked for rather than a re-derivation of it.
		private sealed record StagedTradeskill(INetworkClient Client, TradeskillCommand Request, DateTimeOffset DueAt);

		// STAGE a tradeskill and answer when it will complete. The caller has already sent the behavior ack; retail
		// then goes quiet for the duration, so there is nothing to send here.
		public DateTimeOffset Stage(INetworkClient client, TradeskillCommand request, DateTimeOffset now)
		{
			ArgumentNullException.ThrowIfNull(request);
			var centiseconds = request.Action == TradeskillAction.Socket
				? GemSocketCentiseconds
				: TradeskillReplyCodec.CraftDurationCentiseconds;

			var due = now.AddMilliseconds(centiseconds * 10);
			lock (_staged)
				_staged.Add(new StagedTradeskill(client, request, due));

			return due;
		}

		// Drop everything staged for a connection that is going away, so a craft cannot land on a client that left
		// (or, worse, be granted to a character the session no longer holds). Answers how many were dropped.
		public int Forget(INetworkClient client)
		{
			lock (_staged)
				return _staged.RemoveAll(s => s.Client == client);
		}

		// Run every staged tradeskill whose bar has finished. Called from the world tick, which owns the cadence - the
		// 10 Hz base tick lands the completion within 100 ms of its deadline, against the ~70 ms of jitter the two
		// captured retail transactions themselves carry.
		public async Task CompleteDue(DateTimeOffset now)
		{
			List<StagedTradeskill> due;
			lock (_staged)
			{
				if (_staged.Count == 0)
					return;

				due = _staged.FindAll(s => s.DueAt <= now);
				foreach (var entry in due)
					_staged.Remove(entry);
			}

			foreach (var entry in due)
			{
				// A tradeskill that outlived its connection is not run at all - `Forget` drops those on disconnect, and
				// this is the belt to its braces: a session that has already lost its character cannot be granted to.
				if (entry.Client.Session.Character == null)
					continue;

				switch (entry.Request.Action)
				{
					case TradeskillAction.Craft:
					{
						// Retail answers a SUCCESSFUL craft and says nothing at all otherwise, so a refusal stays
						// silent even now that it arrives five seconds later.
						if (await Craft(entry.Client, entry.Request.RecipeId, entry.Request.Count) is { } crafted)
							await _outbound.SendTradeskillComplete(entry.Client, crafted);

						break;
					}

					case TradeskillAction.Socket:
					{
						await Socket(entry.Client, entry.Request.Target, entry.Request.Gem);
						break;
					}
				}
			}
		}

		// The client's own profession ladders (#735), walked out of the feat prerequisite graph ONCE. Fixed content -
		// the graph comes from the RDB feat table - so a single load holds for the process.
		private ImmutableArray<TradeskillLadder>? _ladders;

		public async Task<ImmutableArray<TradeskillLadder>> Ladders()
		{
			return _ladders ??= TradeskillLadders.Build(await _database.GetSingleFeatPrereqs());
		}

		// What professions a character has trained, and how far - profession NAME -> rank index (0 = Novice). Empty
		// for a character who has trained none, which is every fresh one: retail's recipe book is what a profession
		// BUYS, so no profession means no book at all rather than a book that cannot be used.
		// ⭐ Read from BOTH `CharacterSkills` and `CharacterFeats`, because retail's own answer is in the first of
		// those and ours is in the second. A captured character's professions ride the spawn scaffold's skill list -
		// `CharacterSkills.SkillId` IS a wire feat id, and the imported L80s carry `Armorsmith Grandmaster`,
		// `Mining Master`, `Prospecting Adept` and the rest there - while `.tradeskill` trains through
		// `AddCharacterFeats` like every other feat. Neither list is authoritative over the other; the deepest rung
		// either of them reaches is the rank.
		//
		// ⚠️ Every id is resolved against the LADDERS rather than read off the slot it sits in. Two of the scaffold's
		// slots carry ids that are not professions at all (`328908` Expert Bandaging, `326000` Destiny Quest, both on
		// both captured characters), and a positional reading files those under whatever profession the slot is
		// believed to be - which is exactly how the tier came to look absent from that table.
		public async Task<Dictionary<string, int>> ProfessionsOf(uint characterId)
		{
			var held = (await _database.GetCharacterFeats(characterId)).Concat(await _database.GetCharacterSkillIds(characterId)).ToHashSet();
			var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			if (held.Count == 0)
				return ranks;

			foreach (var ladder in await Ladders())
			{
				if (TradeskillLadders.RankOf(ladder, held) is { } rank)
					ranks[ladder.Name] = rank;
			}

			return ranks;
		}

		// The recipes a character may actually see and craft: those whose `RequiredFeatId` names a rung they have
		// reached (#735). The requirement is the CLIENT's own - every one of the 603 shipped recipes carries one, and
		// they resolve to Weaponsmith/Armorsmith Novice..Master exactly.
		//
		// ⚠️ Compared by DEPTH, never by holding the id. `AddCharacterFeats` keeps the highest rank per `id / 100`
		// base and a ladder spans three bases, so a Weaponsmith Grandmaster holds neither `321000` (Novice) nor
		// `321201` (Master) - a set-membership test would hide most of their own book from them.
		//
		// ⚠️ The WORKSHOP requirement (`RequiredWorkshopFeatId`, 366 of the 603) is deliberately NOT checked: a
		// workshop is a guild-city BUILDING and where the character is standing, not a feat they train, and we have no
		// guild-city model to check against (#761). Enforcing it as if it were a personal feat would refuse every
		// artisan-and-up recipe for a reason that is not the real one.
		public async Task<List<Recipe>> BookFor(uint characterId, IReadOnlyList<Recipe> catalog)
		{
			var ranks = await ProfessionsOf(characterId);
			if (ranks.Count == 0)
				return [];

			var ladders = await Ladders();
			var book = new List<Recipe>();
			foreach (var recipe in catalog)
			{
				foreach (var ladder in ladders)
				{
					if (ladder.RankOfFeat(recipe.RequiredFeatId) is not { } needed)
						continue;

					if (ranks.TryGetValue(ladder.Name, out var held) && held >= needed)
						book.Add(recipe);

					break; // a feat sits on at most one ladder
				}
			}

			return book;
		}

		// Whether the character has trained far enough for this recipe. Same rule as the book above, asked of one
		// row - a recipe whose requirement sits on no ladder at all is ALLOWED, because that is a gap in what we
		// recovered rather than a statement that the character may not craft it.
		public async Task<bool> HasProfessionFor(uint characterId, Recipe recipe)
		{
			var ladders = await Ladders();
			var owning = ladders.FirstOrDefault(l => l.RankOfFeat(recipe.RequiredFeatId) != null);
			if (owning == null)
				return true;

			var ranks = await ProfessionsOf(characterId);
			return ranks.TryGetValue(owning.Name, out var held) && held >= owning.RankOfFeat(recipe.RequiredFeatId)!.Value;
		}

		// Whether the character has trained far enough for a requirement stated as a feat TEMPLATE id (#779). That is
		// the id space retail's own refusal speaks: the `FeatRequired` feedback names `Feats.Id` 894305, not the wire
		// id 323502 the character's feat list carries, so the template -> wire hop happens here rather than at the
		// call site.
		//
		// Same DEPTH comparison as the recipe gate above and the same benefit-of-the-doubt rule: a requirement whose
		// feat sits on no ladder we recovered is ALLOWED, because that is a gap in what we walked out of the feat
		// graph rather than a statement that the character may not do it.
		public async Task<bool> HasReachedFeat(uint characterId, uint templateFeatId)
		{
			if (await _database.GetWireFeatId(templateFeatId) is not { } wireFeatId)
				return true;

			var ladders = await Ladders();
			var owning = ladders.FirstOrDefault(l => l.RankOfFeat(wireFeatId) != null);
			if (owning == null)
				return true;

			var ranks = await ProfessionsOf(characterId);
			return ranks.TryGetValue(owning.Name, out var held) && held >= owning.RankOfFeat(wireFeatId)!.Value;
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

			// #735: the PROFESSION gate. The book below is already filtered, so a client that asks for a recipe it was
			// never sent is either stale or modified - either way it does not get to craft a master-tier item at
			// level 1. Checked before anything is taken or granted, like every other rule here.
			if (!await HasProfessionFor(character.Id, recipe))
			{
				_logger.LogInformation("Craft refused: {Character} has not trained far enough for recipe {Recipe} (needs feat {Feat}).", character.Name, recipeId, recipe.RequiredFeatId);
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

		// ── GATHERING ────────────────────────────────────────────────────────────────────────────────────────────
		// The front half of the tradeskill loop: where a recipe's ingredients come FROM. It lives here rather than in
		// a listener partial because it is the same loop as the craft below - and because the rank gate it opens with
		// is `HasReachedFeat`, this service's own.

		// Harvest a resource node (#494): grant the matching resource stack and retire the node.
		//
		// The captured payoff is the ORDINARY item path - DispatchAddItem into the backpack plus a
		// DispatchDisplayItemLootChatMessage carrying (itemId, itemId, 0, count) - so nothing new goes on the wire, and
		// the resource items already ship in faolan.db.
		//
		// ONE INTERACTION IS ONE SWING, and a swing takes 100 of the node's 1000 health and grants exactly 1 unit - so
		// a full node is ten swings, not one. This used to empty the node in a single use and hand out 40, both of
		// which were misreadings the annotated mining session settles:
		//
		//   - "one harvest empties it" came from reading the ENDS of the earlier captures and explaining the partial
		//     values (901, 701, 382, ...) away as OTHER players on a shared node. They are the per-swing decrement.
		//     The maintainer's own gold vein runs 298 -> 198 -> 98 while they narrate "-10% each time".
		//   - the 40 came from the 4th u32 of the DispatchDisplayItemLootChatMessage that follows. That word is the
		//     item's LEVEL (see HarvestCatalog.Yields); the count rides the ADD's stat_list 26, and it is 1 every time.
		//
		// The client renders health/max as the PERCENTAGE in the node's health bar - which is where the "12% silver
		// vein" / "30% gold vein" annotations come from - so the swing has to put the new health on the wire or the
		// node keeps reading full. That is the same StatIIR the respawn sequence already sends for CarsGroup.
		// [V2: NecroL80_mage-necromancer_login_gameplay_annotated_5 18:22-18:45, dynels 0x2688/0x26d6/0x269a/0x2666/0x26c0]
		//
		// Retail also REGROWS a node in place (+33/+26/+16 per 60s, per node) rather than despawning it at the floor.
		// We still despawn-and-respawn on exhaustion, which is the existing DynelVisualSequence; regrowth-in-place is
		// tracked in Gitea #732.
		public async Task Harvest(INetworkClient client, LiveWorldObject node)
		{
			var character = client.Session.Character;

			// ⚠️ The node is identified by its DISPLAY id, not by Row.Name (#734). The record's name STRING is an
			// internal name retail itself gets wrong - the silver vein ships as "Martial Ring", a copper node as
			// "Vitalic Bronze-studded Armbands" - so keying on it left those rows silently inert. `Name` here is the
			// LDB name the player actually sees, which is also what the messages below should say.
			if (HarvestCatalog.NodeFor(node.Row.DisplayCategory, node.Row.DisplayId) is not { } harvest)
				return; // not a harvest node at all - some other destructible the player clicked

			var name = harvest.Name;

			// #779: the GATHERING gate, and it goes FIRST because it is the only refusal here that is retail's own -
			// retail turns a player away who has not trained the rank and says WHICH feat is missing rather than "you
			// cannot do that", as one FormatFeedbackIIR carrying the `FeatRequired` remoteformat with the feat's
			// template id in it. It also has to precede the #746 branch below, because the one node whose requirement
			// the corpus states (Marble) is also one of the nine with no resource item - checking that first would
			// answer a rank refusal with our own placeholder and the gate would never run at all.
			//
			// ⚠️ Only ONE node type carries a requirement (see HarvestCatalog.HarvestNode.RequiredFeatId): the corpus
			// states exactly one, and deriving the other 24 from the tier ordering is the invention that catalog exists
			// to avoid. Every other node stays ungated, which is the behaviour that shipped before this gate.
			if (harvest.RequiredFeatId is { } requiredFeat && !await HasReachedFeat(character.Id, requiredFeat))
			{
				_logger.LogInformation("Harvest: {Player} has not trained feat {Feat}, so '{Node}' 0x{Handle:X8} was refused.", character.Name, requiredFeat, name, node.Handle);
				await WorldIir.Send(client, FormatFeedbackCodec.Build(character.Id, new FeatRequired(requiredFeat)));

				return;
			}

			// #746: a node type whose resource has no item row. Nine of the 25 shipped types are in this state, and it
			// is not something a player can be expected to infer from a click that does nothing - so say it, the same
			// way the exhausted and bag-full refusals do. Never silent: the alternative reads as a broken node.
			if (harvest.ResourceItemId is not { } resourceId)
			{
				_logger.LogInformation("Harvest: '{Node}' 0x{Handle:X8} has no resource item in the catalog, so {Player} was granted nothing (Gitea #746/#203).",
					name, node.Handle, character.Name);
				await _chat.CharacterSay(node.Handle, $"({name}: not yet harvestable)", [character.Id]);
				return;
			}

			if (!node.IsHarvestable)
			{
				// Never silent: a clickable but exhausted node means a despawn we failed to deliver.
				_logger.LogInformation("Harvest: '{Node}' 0x{Handle:X8} is already exhausted; nothing granted to {Player}.", name, node.Handle, character.Name);
				await _chat.CharacterSay(node.Handle, $"({name}: already harvested)", [character.Id]);
				return;
			}

			var count = HarvestCatalog.YieldFor(node.Row.DisplayId);
			if (await _outbound.GrantItemToBackpack(client, resourceId, count) == null)
			{
				// Bag full: refuse the harvest ENTIRELY rather than consuming the node for nothing. The node keeps its
				// health, so the player can come back - the same grant-before-consume order the shop's buy path uses.
				_logger.LogInformation("Harvest: {Player}'s bag is full, so '{Node}' was not harvested (it keeps its health).", character.Name, name);
				await _chat.CharacterSay(node.Handle, "(bag full)", [character.Id]);
				return;
			}

			var before = node.Health ?? HarvestCatalog.NodeMaxHealth;
			var after = before - HarvestCatalog.SwingCost;
			node.Health = after;
			await SendItemLootChatMessage(client, resourceId, (uint)((await _database.GetItem(resourceId))?.Level ?? 0));
			await _worldObjects.BroadcastNodeHealth(client, node, after);

			// Exhausted only when it can no longer pay for a swing. Until then the node stays in the world with its
			// health bar down, which is what the player sees in retail.
			if (after >= HarvestCatalog.SwingCost)
			{
				_logger.LogInformation("Harvest: {Player} took {Count}x item {Item} from '{Node}' 0x{Handle:X8}; health {Before} -> {After} ({Percent}%).",
					character.Name, count, resourceId, name, node.Handle, before, after, after * 100 / HarvestCatalog.NodeMaxHealth);
				return;
			}

			node.Health = HarvestCatalog.ExhaustedHealth;
			await _worldObjects.DespawnHarvestedNode(client, node);

			_logger.LogInformation("Harvest: {Player} took the last {Count}x item {Item} from '{Node}' 0x{Handle:X8}; node exhausted and despawned.",
				character.Name, count, resourceId, name, node.Handle);
		}

		// Decoded from the harvesting span of the chat-annotated capture (#494), where the loot line lands beside its
		// annotation each time - Granite 161206 x40 (`just mined`), Oak 161213 x60 (`gethering wood`), Iron 161196 x40
		// (`mining iron`). The item id is repeated in the first two words in every observed record.
		// ⚠️ The 4th word is the item's LEVEL, not a count. The body is the same ProtoAcgItem-shaped layout the
		// client's `itemref://` chat link carries - [template][template][0][level][hash][key1][key2][key3] - and
		// reading it as a count is what put the bogus 40 / 60 / 40 yields into HarvestCatalog (#732): Items.Level for
		// Granite, Oak and Iron is 40, 60 and 40 exactly. The line renders an item LINK, so the level is what its
		// tooltip reads; how many arrived is the ADD's business, not this message's.
		// The 48-byte tail is the hash + the three key quads, which we do not fill - the link still renders.
		private static Task SendItemLootChatMessage(INetworkClient client, uint itemId, uint level)
		{
			return client.SendAsync(new Rpc(GameServerListener.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType),
				GameCharInterfaceMethods.DispatchDisplayItemLootChatMessage,
				b => b.WriteUInt32(itemId).WriteUInt32(itemId).WriteUInt32(0u).WriteUInt32(level).WriteArray(new byte[48])));
		}
	}
}
