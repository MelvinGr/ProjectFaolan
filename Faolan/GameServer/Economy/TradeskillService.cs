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
using Faolan.GameServer.Wire;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Economy
{
	/// <summary>
	///     Crafting and gathering: turning a recipe's ingredients into its product, and the harvest loop the
	///     ingredients come from.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The whole transaction is captured in NecroL80_mage-necromancer_login_gameplay_annotated_5, and every step
	///         below is one of its lines - the player linked the item in <c>/say</c> before and after, which is how
	///         the craft is locatable at all:
	///     </para>
	///     <code>
	///     18:45:25 c2s BehaviorControlCenter  BehaviorId.Tradeskill (0x27), tail [flag 1][recipe 15][count 1]
	///     18:45:26 s2c   the behavior ack
	///     18:45:30 s2c   DispatchSetItemStat x3   the destination cell's StaticInstance/Duration bookkeeping
	///                    DispatchAddItem          the product, carrying the crafter's creator_name
	///                    DispatchRemoveItem       bag 0x68 pos 19      - Plain Hilt Piece, consumed whole
	///                    DispatchRemoveItem       resource 0x70 pos 13 - Copper, consumed whole
	///                    DispatchSetItemStat      resource 0x70 pos 10 StackSize 2 - Silver, decremented
	///                    DispatchTradeskillComplete 0x0000000F
	///     </code>
	///     <para>
	///         The <c>0x0F</c> is the recipe id, and recipe 15 in the shipped catalog is exactly Plain Hilt Piece x1 +
	///         Copper x4 + Silver x4 to a Bloodsilver Khopesh. The removals corroborate the quantities to the unit:
	///         the copper stack runs 4 to 0 and the silver stack 6 to 2. The shipped Recipes and RecipeItems rows are
	///         retail's own, harvested from the s2c TradeskillReplyIIR - retail telling the client what its book
	///         holds, ingredients and product included - and there is no client-side recipe table to fall back on.
	///         [V2: NecroL80_mage-necromancer_login_gameplay_annotated_5 fr17374/17376/17401/17402]
	///     </para>
	///     <para>
	///         Retail paces the transaction, and the pace is the client's own data. A tradeskill is a command-queue
	///         command (<c>TradeskillCommand_c</c>) whose duration getter (<c>FUN_0114bf50</c>) answers per operation:
	///         a craft returns the recipe record's <c>+0x24</c>, which is the mid-header word
	///         <c>TradeskillReplyCodec.CraftDurationCentiseconds</c> (<c>0x1F4</c> on every one of the 603 shipped
	///         recipes); a gem add returns a hardcoded 500; a gem remove 0; anything else 100. The unit is
	///         centiseconds, the same as the BCC's <c>activation_time</c>, and it sizes the client's
	///         <c>TradeskillProgressBarDesc</c> bar. Both numbers are confirmed server-side on the wire in two
	///         different captures, so the hold is real pacing rather than a client-only animation:
	///     </para>
	///     <code>
	///     craft  ..._annotated_5  fr17374 -&gt; fr17402   18:45:25.441 -&gt; 18:45:30.801, RTT/2 ~140 ms -&gt; 5.07 s
	///     socket ..._annotated_8  fr8215  -&gt; fr8248    06:52:55.276 -&gt; 06:53:00.384, RTT/2  ~65 ms -&gt; 4.98 s
	///     </code>
	///     <para>
	///         So the inbound handler stages the transaction (<see cref="Stage" />) and the world tick runs it once
	///         its deadline passes (<see cref="CompleteDue" />). Nothing is checked, taken or granted until then,
	///         which is retail's order too: the completion decides, which is why a player who empties their own bag
	///         mid-bar is refused at the end of the bar rather than at its start.
	///     </para>
	///     <para>
	///         A character must have trained a tradeskill to see or use a recipe, and the model is the client's own: a
	///         profession is a strict linear chain of feats in <c>FeatPrereqs</c>, seven ranks deep, and every one of
	///         the 603 recipes states the rung it needs in <c>RequiredFeatId</c>. <c>TradeskillLadders</c> walks the
	///         chains, <see cref="BookFor" /> filters the book to what the character has reached, and
	///         <c>HasProfessionFor</c> gates the craft itself. A character who has trained nothing gets an empty book,
	///         which is retail: the book is what a profession buys.
	///     </para>
	///     <para>
	///         No captured character carries a tradeskill feat, and yet the captured level-80 crafted a Weaponsmith
	///         recipe, so the profession does not ride the spawn scaffold. What expresses it on the wire is the book:
	///         the 603 shipped rows were harvested from that one character's <c>TradeskillReplyIIR</c>, which is why
	///         they are Weaponsmith and Armorsmith and nothing else - the catalog is one player's book. The retail
	///         learn path is uncaptured, and <c>.tradeskill</c> is the GM bootstrap standing in for it.
	///     </para>
	/// </remarks>
	public sealed class TradeskillService
	{
		// The client's hardcoded duration for putting a gem in (`FUN_0114bf50` case 2), in centiseconds. Removing one
		// is 0 there - instant - and removal is not implemented, so it needs no constant.
		public const uint GemSocketCentiseconds = 500;

		// Gathering's wire side: a swung node restates its health to everyone looking at it, and an exhausted one
		// despawns. Both are ordinary world-object broadcasts, which is the whole of what this service needs from the
		// world, so it takes the object service rather than the broadcaster.
		private readonly WorldObjectService _worldObjects;

		// The refusal lines a harvest can produce ("already harvested", "bag full", "not yet harvestable"), said by the
		// node, which is how retail words them, rather than as a system message.
		private readonly IChatServerDispatcher _chat;
		private readonly ICatalogRepository _catalog;
		private readonly ICharacterInventoryRepository _inventory;
		private readonly ICharacterProgressionRepository _progression;
		private readonly ILogger _logger;
		private readonly IEconomyOutbound _outbound;

		// The staged tradeskills, oldest first. A plain list under a lock rather than a per-client slot, because the
		// client's own command queue can hold more than one and dropping a second craft would silently eat it.
		private readonly List<StagedTradeskill> _staged = [];

		public TradeskillService(ICatalogRepository catalog, ICharacterInventoryRepository inventory, ICharacterProgressionRepository progression, IEconomyOutbound outbound, WorldObjectService worldObjects, IChatServerDispatcher chat, ILogger logger)
		{
			_catalog = catalog;
			_inventory = inventory;
			_progression = progression;
			_outbound = outbound;
			_worldObjects = worldObjects;
			_chat = chat;
			_logger = logger;
		}

		// One tradeskill waiting out its bar. `Request` is the parsed command verbatim, so the completion runs exactly
		// what was asked for rather than a re-derivation of it.
		private sealed record StagedTradeskill(INetworkClient Client, TradeskillCommand Request, DateTimeOffset DueAt);

		/// <summary>
		///     Stages a tradeskill and answers when it will complete. The caller has already sent the behavior ack, and
		///     retail then goes quiet for the duration, so there is nothing to send here.
		/// </summary>
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

		/// <summary>
		///     Drops everything staged for a connection that is going away, so a craft cannot land on a client that
		///     left, or be granted to a character the session no longer holds.
		/// </summary>
		/// <returns>How many were dropped.</returns>
		public int Forget(INetworkClient client)
		{
			lock (_staged)
				return _staged.RemoveAll(s => s.Client == client);
		}

		/// <summary>
		///     Runs every staged tradeskill whose bar has finished.
		/// </summary>
		/// <remarks>
		///     Called from the world tick, which owns the cadence: the 10 Hz base tick lands the completion within
		///     100 ms of its deadline, against the roughly 70 ms of jitter the two captured retail transactions
		///     themselves carry.
		/// </remarks>
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
				// A tradeskill that outlived its connection is not run at all. `Forget` drops those on disconnect; this
				// is the second guard, since a session that has lost its character cannot be granted to.
				if (entry.Client.Session.Character == null)
					continue;

				switch (entry.Request.Action)
				{
					case TradeskillAction.Craft:
					{
						// Retail answers a successful craft and says nothing otherwise, so a refusal stays silent even
						// though it arrives five seconds later.
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

		// The client's own profession ladders, walked out of the feat prerequisite graph once. Fixed content - the
		// graph comes from the RDB feat table - so a single load holds for the process.
		private ImmutableArray<TradeskillLadder>? _ladders;

		public async Task<ImmutableArray<TradeskillLadder>> Ladders()
		{
			return _ladders ??= TradeskillLadders.Build(await _progression.GetSingleFeatPrereqs());
		}

		/// <summary>
		///     What professions a character has trained, and how far: profession name to rank index, 0 being Novice.
		/// </summary>
		/// <remarks>
		///     <para>
		///         Empty for a character who has trained none, which is every fresh one: retail's recipe book is what a
		///         profession buys, so no profession means no book at all rather than a book that cannot be used.
		///     </para>
		///     <para>
		///         Read from both <c>CharacterSkills</c> and <c>CharacterFeats</c>, because retail's own answer is in
		///         the first and this server's is in the second. A captured character's professions ride the spawn
		///         scaffold's skill list - <c>CharacterSkills.SkillId</c> is a wire feat id, and the imported level-80s
		///         carry <c>Armorsmith Grandmaster</c>, <c>Mining Master</c>, <c>Prospecting Adept</c> and the rest
		///         there - while <c>.tradeskill</c> trains through <c>AddCharacterFeats</c> like every other feat.
		///         Neither list is authoritative over the other: the deepest rung either reaches is the rank.
		///     </para>
		///     <para>
		///         Every id is resolved against the ladders rather than read off the slot it sits in. Two of the
		///         scaffold's slots carry ids that are not professions at all (<c>328908</c> Expert Bandaging,
		///         <c>326000</c> Destiny Quest, both on both captured characters), and a positional reading would file
		///         those under whatever profession the slot is believed to be.
		///     </para>
		/// </remarks>
		public async Task<Dictionary<string, int>> ProfessionsOf(uint characterId)
		{
			var held = (await _progression.GetCharacterFeats(characterId)).Concat(await _progression.GetCharacterSkillIds(characterId)).ToHashSet();
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

		/// <summary>
		///     The recipes a character may actually see and craft: those whose <c>RequiredFeatId</c> names a rung they
		///     have reached.
		/// </summary>
		/// <remarks>
		///     <para>
		///         The requirement is the client's own - every one of the 603 shipped recipes carries one, and they
		///         resolve to Weaponsmith and Armorsmith Novice through Master exactly.
		///     </para>
		///     <para>
		///         Compared by depth, never by holding the id. <c>AddCharacterFeats</c> keeps the highest rank per
		///         <c>id / 100</c> base and a ladder spans three bases, so a Weaponsmith Grandmaster holds neither
		///         <c>321000</c> (Novice) nor <c>321201</c> (Master), and a set-membership test would hide most of
		///         their own book from them.
		///     </para>
		///     <para>
		///         The workshop requirement (<c>RequiredWorkshopFeatId</c>, on 366 of the 603) is deliberately not
		///         checked: a workshop is a guild-city building and a question of where the character is standing,
		///         not a feat they train, and there is no guild-city model to check against. Enforcing it as if it
		///         were a personal feat would refuse every artisan-and-up recipe for a reason that is not the real
		///         one. Open work tracked in Gitea #761.
		///     </para>
		/// </remarks>
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

		/// <summary>
		///     Whether the character has trained far enough for this recipe - the same rule as the book above, asked of
		///     one row.
		/// </summary>
		/// <remarks>
		///     A recipe whose requirement sits on no ladder at all is allowed, because that is a gap in what was
		///     recovered rather than a statement that the character may not craft it.
		/// </remarks>
		public async Task<bool> HasProfessionFor(uint characterId, Recipe recipe)
		{
			var ladders = await Ladders();
			var owning = ladders.FirstOrDefault(l => l.RankOfFeat(recipe.RequiredFeatId) != null);
			if (owning == null)
				return true;

			var ranks = await ProfessionsOf(characterId);
			return ranks.TryGetValue(owning.Name, out var held) && held >= owning.RankOfFeat(recipe.RequiredFeatId)!.Value;
		}

		/// <summary>
		///     Whether the character has trained far enough for a requirement stated as a feat template id.
		/// </summary>
		/// <remarks>
		///     That is the id space retail's own refusal speaks: the <c>FeatRequired</c> feedback names <c>Feats.Id</c>
		///     894305, not the wire id 323502 the character's feat list carries, so the template-to-wire hop happens
		///     here rather than at the call site. Same depth comparison as the recipe gate above, and the same
		///     benefit-of-the-doubt rule: a requirement whose feat sits on no recovered ladder is allowed.
		/// </remarks>
		public async Task<bool> HasReachedFeat(uint characterId, uint templateFeatId)
		{
			if (await _progression.GetWireFeatId(templateFeatId) is not { } wireFeatId)
				return true;

			var ladders = await Ladders();
			var owning = ladders.FirstOrDefault(l => l.RankOfFeat(wireFeatId) != null);
			if (owning == null)
				return true;

			var ranks = await ProfessionsOf(characterId);
			return ranks.TryGetValue(owning.Name, out var held) && held >= owning.RankOfFeat(wireFeatId)!.Value;
		}

		/// <summary>
		///     Puts a gem into a socket of another item - the <c>Tradeskill</c> command's second operation.
		/// </summary>
		/// <returns>True when a gem was actually socketed.</returns>
		/// <remarks>
		///     A refusal is silent, like a refused craft: retail answers a successful tradeskill and says nothing
		///     otherwise, so telling the client about a rejection would be inventing a message. Socketing consumes the
		///     gem, which is why every rule is checked before the write rather than trusting the client to have offered
		///     a legal combination - the client does validate, drawing the slots and their colours, so these checks are
		///     the defence against a modified one.
		/// </remarks>
		public async Task<bool> Socket(INetworkClient client, InventoryAddress target, InventoryAddress gem)
		{
			var character = client.Session.Character;
			if (target.Container != gem.Container || target.Slot == gem.Slot)
				return false; // both addresses are cells of the player's own bag in every captured socketing

			var host = await _inventory.GetCharacterItemSlot(character.Id, target.Container, (int)target.Slot);
			var stone = await _inventory.GetCharacterItemSlot(character.Id, gem.Container, (int)gem.Slot);
			if (host == null || stone == null)
			{
				_logger.LogInformation("Socket refused: {Character} named {Container}[{Target}] + [{Gem}] and one of them is empty.", character.Name, target.Container, target.Slot,
					gem.Slot);
				return false;
			}

			// How many sockets the item has - `NumGemSlots`, template field 114. Zero means it takes no gems at all,
			// and socketing into it would destroy the gem for nothing.
			var slots = host.Template.GemSlots;
			if (slots == 0)
			{
				_logger.LogInformation("Socket refused: {Item} has no gem sockets.", host.Template.Name);
				return false;
			}

			// The first free index. An item may already hold more gems than it declares - a state the client itself
			// calls GemOverflow - so this bounds what may be added and deliberately does not judge what is already
			// there.
			var taken = host.Sockets.Select(x => x.SocketIndex).ToHashSet();
			var index = Enumerable.Range(0, (int)slots).Select(i => (uint)i).Cast<uint?>().FirstOrDefault(i => !taken.Contains(i!.Value));
			if (index == null)
			{
				_logger.LogInformation("Socket refused: {Item} has all {Slots} socket(s) filled ({Held} gem(s) held).", host.Template.Name, slots, host.Sockets.Count);
				return false;
			}

			var socket = await _inventory.SocketGem(host.Id, index.Value, stone.Id);
			if (socket == null)
				return false;

			await _outbound.SendGemSocketed(client, target.Container, target.Slot, socket, gem.Slot);
			_logger.LogInformation("Socketed {Gem} into {Item} slot {Index} for {Character}.", stone.Template.Name, host.Template.Name, index.Value, character.Name);

			return true;
		}

		/// <summary>
		///     Runs one craft.
		/// </summary>
		/// <returns>
		///     The recipe id to echo in DispatchTradeskillComplete, or null when nothing was made - the caller stays
		///     silent then, because retail's completion means "it worked" and sending one anyway would tell the client
		///     to close a window over an item it never received.
		/// </returns>
		public async Task<uint?> Craft(INetworkClient client, uint recipeId, uint count)
		{
			var character = client.Session.Character;
			if (count == 0)
				return null;

			var recipe = await _catalog.GetRecipe(recipeId);
			if (recipe == null)
			{
				_logger.LogInformation("Craft refused: {Character} asked for recipe {Recipe}, which is not in the catalog.", character.Name, recipeId);
				return null;
			}

			// The profession gate. The book below is already filtered, so a client asking for a recipe it was never
			// sent is either stale or modified, and either way does not get to craft a master-tier item at level 1.
			// Checked before anything is taken or granted, like every other rule here.
			if (!await HasProfessionFor(character.Id, recipe))
			{
				_logger.LogInformation("Craft refused: {Character} has not trained far enough for recipe {Recipe} (needs feat {Feat}).", character.Name, recipeId, recipe.RequiredFeatId);
				return null;
			}

			var product = recipe.Items.FirstOrDefault(i => i.Role == RecipeItemRole.Result);
			if (product == null)
			{
				// A recipe with no result is a harvest artefact rather than a craft, so refuse instead of consuming the
				// ingredients for nothing.
				_logger.LogWarning("Craft refused: recipe {Recipe} has no result item.", recipeId);
				return null;
			}

			// Ingredients are summed by template first: a recipe listing the same item twice, or the same material
			// under two positions, must be checked and taken as one total, or a two-position entry passes a per-row
			// check while the second take comes up short.
			var needed = new Dictionary<uint, uint>();
			foreach (var ingredient in recipe.Items.Where(i => i.Role == RecipeItemRole.Ingredient))
			{
				// A recipe element's `Trailing` is its quantity. The captured rows use 0 only on the result, so an
				// ingredient reading 0 means one - the same reading the inventory walk uses for a stackless cell.
				var quantity = ingredient.Trailing == 0 ? 1u : ingredient.Trailing;
				needed[ingredient.ItemId] = needed.GetValueOrDefault(ingredient.ItemId) + (quantity * count);
			}

			// Check everything before taking anything: taking as it goes would leave a player who is one silver short
			// having paid the copper. The same grant-before-consume discipline the shop's buy path and the harvest
			// bag-full refusal use.
			var held = await _inventory.GetCharacterItems(character.Id);
			foreach (var (itemId, quantity) in needed)
			{
				var have = held.Where(i => i.TemplateId == itemId).Aggregate(0u, (sum, i) => sum + (i.Count == 0 ? 1u : i.Count));
				if (have >= quantity)
					continue;

				_logger.LogInformation("Craft refused: {Character} holds {Have} of item {Item} but recipe {Recipe} needs {Need}.", character.Name, have, itemId, recipeId, quantity);
				return null;
			}

			// The product first, so a full bag refuses the craft with the materials still in it.
			var productItem = await _catalog.GetItem(product.ItemId);
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
					// The pre-check passed, so a short take means the inventory moved underneath it. Worth a line: the
					// player has been undercharged for an item they already hold.
					_logger.LogWarning("Craft: took only {Taken} of {Need} item {Item} for {Character}'s recipe {Recipe} - inventory changed mid-craft.", taken, quantity, itemId, character.Name,
						recipeId);
				}
			}

			_logger.LogInformation("Craft: {Character} made {Count}x {Product} ({ProductId}) from recipe {Recipe}, into bag slot {Slot}.", character.Name, count, productItem?.Name ?? "?",
				product.ItemId, recipeId, slot);
			return recipeId;
		}

		/// <summary>
		///     Harvests a resource node: grants the matching resource stack and retires the node.
		/// </summary>
		/// <remarks>
		///     <para>
		///         Gathering is the front half of the tradeskill loop - where a recipe's ingredients come from - and
		///         lives here because it is the same loop as the craft, and because the rank gate it opens with is
		///         this service's own <see cref="HasReachedFeat" />.
		///     </para>
		///     <para>
		///         The captured payoff is the ordinary item path: DispatchAddItem into the backpack plus a
		///         DispatchDisplayItemLootChatMessage, so nothing new goes on the wire and the resource items already
		///         ship in the database.
		///     </para>
		///     <para>
		///         One interaction is one swing, and a swing takes 100 of the node's 1000 health and grants exactly one
		///         unit, so a full node is ten swings. The partial health values in the captures (901, 701, 382, ...)
		///         are that per-swing decrement rather than other players on a shared node: one annotated gold vein
		///         runs 298 to 198 to 98 while the player narrates "-10% each time". The count rides the add's
		///         stat_list 26 and is 1 every time; the fourth u32 of the DispatchDisplayItemLootChatMessage that
		///         follows is the item's level, not a count - see <c>HarvestCatalog.Yields</c>.
		///     </para>
		///     <para>
		///         The client renders health over max as the percentage in the node's health bar, which is where the
		///         "12% silver vein" and "30% gold vein" annotations come from, so the swing has to put the new health
		///         on the wire or the node keeps reading full. That is the same StatIIR the respawn sequence sends for
		///         CarsGroup.
		///         [V2: NecroL80_mage-necromancer_login_gameplay_annotated_5 18:22-18:45, dynels
		///         0x2688/0x26d6/0x269a/0x2666/0x26c0]
		///     </para>
		///     <para>
		///         Retail also regrows a node in place (+33/+26/+16 per 60 s, per node) rather than despawning it at
		///         the floor. This despawns and respawns on exhaustion instead. Open work tracked in Gitea #732.
		///     </para>
		/// </remarks>
		public async Task Harvest(INetworkClient client, LiveWorldObject node)
		{
			var character = client.Session.Character;

			// The node is identified by its display id rather than by the row's name. The record's name string is an
			// internal name retail itself gets wrong - the silver vein ships as "Martial Ring", a copper node as
			// "Vitalic Bronze-studded Armbands" - so keying on it leaves those rows silently inert. `Name` here is the
			// LDB name the player sees, which is also what the messages below say.
			if (HarvestCatalog.NodeFor(node.Row.DisplayCategory, node.Row.DisplayId) is not { } harvest)
				return; // not a harvest node at all - some other destructible the player clicked

			var name = harvest.Name;

			// The gathering gate goes first, because it is the only refusal here that is retail's own: retail turns
			// away a player who has not trained the rank and says which feat is missing, as one FormatFeedbackIIR
			// carrying the `FeatRequired` remoteformat with the feat's template id in it. It also has to precede the
			// missing-resource branch below, because the one node whose requirement the corpus states (Marble) is also
			// one of the nine with no resource item, and checking that first would answer a rank refusal with a
			// placeholder and the gate would never run.
			//
			// Only one node type carries a requirement (see HarvestCatalog.HarvestNode.RequiredFeatId): the corpus
			// states exactly one, and deriving the other 24 from the tier ordering is the invention that catalog
			// exists to avoid. Every other node stays ungated.
			if (harvest.RequiredFeatId is { } requiredFeat && !await HasReachedFeat(character.Id, requiredFeat))
			{
				_logger.LogInformation("Harvest: {Player} has not trained feat {Feat}, so '{Node}' 0x{Handle:X8} was refused.", character.Name, requiredFeat, name, node.Handle);
				await WorldIir.Send(client, FormatFeedbackCodec.Build(character.Id, new FeatRequired(requiredFeat)));

				return;
			}

			// A node type whose resource has no item row. Nine of the 25 shipped types are in this state, and a player
			// cannot infer that from a click that does nothing, so it is said the same way the exhausted and bag-full
			// refusals are. Never silent: the alternative reads as a broken node.
			if (harvest.ResourceItemId is not { } resourceId)
			{
				_logger.LogInformation("Harvest: '{Node}' 0x{Handle:X8} has no resource item in the catalog, so {Player} was granted nothing (Gitea #746/#203).",
					name, node.Handle, character.Name);
				await _chat.CharacterSay(node.Handle, $"({name}: not yet harvestable)", [character.Id]);
				return;
			}

			if (!node.IsHarvestable)
			{
				// Never silent: a clickable but exhausted node means a despawn that was not delivered.
				_logger.LogInformation("Harvest: '{Node}' 0x{Handle:X8} is already exhausted; nothing granted to {Player}.", name, node.Handle, character.Name);
				await _chat.CharacterSay(node.Handle, $"({name}: already harvested)", [character.Id]);
				return;
			}

			var count = HarvestCatalog.YieldFor(node.Row.DisplayId);
			if (await _outbound.GrantItemToBackpack(client, resourceId, count) == null)
			{
				// Bag full: refuse the harvest entirely rather than consuming the node for nothing. The node keeps its
				// health, so the player can come back - the same grant-before-consume order the shop's buy path uses.
				_logger.LogInformation("Harvest: {Player}'s bag is full, so '{Node}' was not harvested (it keeps its health).", character.Name, name);
				await _chat.CharacterSay(node.Handle, "(bag full)", [character.Id]);
				return;
			}

			var before = node.Health ?? HarvestCatalog.NodeMaxHealth;
			var after = before - HarvestCatalog.SwingCost;
			node.Health = after;
			await SendItemLootChatMessage(client, resourceId, (uint)((await _catalog.GetItem(resourceId))?.Level ?? 0));
			await _worldObjects.BroadcastNodeHealth(client, node, after);
			await _worldObjects.BroadcastGatheringPin(client, node); // the map pin flips to "being gathered"

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

		/// <summary>
		///     The item-link chat line a harvest produces, decoded from the harvesting span of the chat-annotated
		///     capture, where the loot line lands beside its annotation each time.
		/// </summary>
		/// <remarks>
		///     The item id is repeated in the first two words of every observed record, and the fourth word is the
		///     item's level rather than a count: the body is the same ProtoAcgItem-shaped layout the client's
		///     <c>itemref://</c> chat link carries - [template][template][0][level][hash][key1][key2][key3] - and
		///     Items.Level for Granite, Oak and Iron is 40, 60 and 40, which is exactly what a count reading of that
		///     word would invent. The line renders an item link, so the level is what its tooltip reads; how many
		///     arrived is the add's business. The 48-byte tail is the hash and the three key quads, left unfilled -
		///     the link still renders.
		/// </remarks>
		private static Task SendItemLootChatMessage(INetworkClient client, uint itemId, uint level)
		{
			return client.SendAsync(new Rpc(GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType),
				GameCharInterfaceMethods.DispatchDisplayItemLootChatMessage,
				b => b.WriteUInt32(itemId).WriteUInt32(itemId).WriteUInt32(0u).WriteUInt32(level).WriteArray(new byte[48])));
		}
		/// <summary>
		///     Answers a TradeskillRequestIIR (recipe-book open) with the TradeskillReplyIIR (0x9062D559) recipe list
		///     built from the Recipe catalog, so the Crafting window's Recipes tab populates.
		/// </summary>
		/// <remarks>
		///     The carrier is the standard world IIR framing (GameAgent 0x58 to GameInterface at the client's
		///     instance), with the body [subop][0xC350][handle][reply], byte-exact against the capture
		///     (docs/subsystems/tradeskill-recipes.md). An empty catalog yields an empty list, which opens the book
		///     cleanly with no recipes.
		/// </remarks>
		public async Task SendCraftingRecipeBook(INetworkClient client)
		{
			var handle = client.Session.Character.Id;

			// Only what the character has trained for. A fresh character has learned no profession and so gets an empty
			// book, which is retail: the book is what a profession buys. The gate is the recipe's own `RequiredFeatId`
			// against the character's rank on that profession's ladder.
			var rows = await this.BookFor(handle, await _catalog.GetRecipes());
			var records = rows.Select(RecipeRecords.FromRow).ToList();

			await client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(TradeskillReplyCodec.Build(handle, records))));

			_logger.LogInformation("Sent crafting recipe book: {Count} recipe(s) to client {Handle} (TradeskillReplyIIR).", records.Count, handle);
		}
	}
}
