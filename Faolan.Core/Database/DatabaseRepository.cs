using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database
{
	public interface IDatabaseRepository
	{
		Task AddCharacterTag(uint characterId, uint tagId);
		Task<bool> CheckLogin(string username, string password);
		Task<Character> CreateCharacter(uint accountId, uint realmId);

		Task<bool> DeleteCharacter(uint characterId);
		Task DiscoverRespawnPoint(uint characterId, uint respawnPointId);

		// Ad-hoc unit of work: runs `work` against a fresh short-lived context and disposes it. Use for one-off
		// reads/writes that do not have a dedicated method. Entities returned are detached.
		Task Execute(Func<IDatabaseContext, Task> work);
		Task<Ability[]> GetAbilities();

		Task<Ability> GetAbility(uint id);

		Task<Account> GetAccount(uint id);
		Task<Account> GetAccount(string userName);

		Task<Character> GetCharacter(uint id);
		Task<Character> GetCharacterByName(string name);

		Task<CharacterEquipment> GetCharacterEquipment(uint characterId);
		Task<Character> GetCharacterForAccount(uint accountId, uint clientInstance);
		Task<CharacterInventory[]> GetCharacterInventory(uint characterId);
		Task<CharacterQuest[]> GetCharacterQuests(uint characterId);
		Task<Dictionary<string, uint>> GetQuestGiverMap();
		Task<Dictionary<string, uint[]>> GetQuestGatedNpcMap();
		Task<CharacterSpell[]> GetCharacterSpells(uint characterId);
		Task<bool> AddCharacterSpell(uint characterId, uint spellId);
		Task<QuestDefinition> GetQuestDefinition(uint questId);
		Task<Character[]> GetCharactersByAccount(uint id, bool skipUninitialized = true);
		Task<Character> GetCharacterScaffold(uint characterId);
		Task<CharacterShortcut[]> GetCharacterShortcuts(uint characterId);
		Task<uint[]> GetCharacterTags(uint characterId);
		Task RemoveCharacterShortcut(uint characterId, uint slot);
		Task SetCharacterShortcut(uint characterId, uint slot, uint shortcutType, uint targetId);

		Task<ConversationNode> GetConversationScreen(string npcName, uint typeId, string answerPath);
		Task<List<ConversationNode>> GetConversationScreens(string npcName, uint typeId, string answerPath);
		Task<uint[]> GetDiscoveredRespawnPointIds(uint characterId);

		Task<Guild> CreateGuild(string name, uint leaderCharacterId);
		Task<Guild> GetGuild(uint id);
		Task<Guild> GetGuildByName(string name);
		Task SaveGuild(Guild guild);
		Task<bool> SetCharacterGuild(uint characterId, uint? guildId, byte rank);

		Task<Item> GetItem(uint id);
		Task<Item[]> SearchItems(string nameSubstring, int limit = 25);
		Task<Item[]> GetRandomBuildableItems(int count);
		Task<NpcSpawn[]> SearchNpcSpawns(string nameSubstring, int limit = 50);
		Task<LuaScript> GetLuaScript(uint scriptId);

		Task<Map> GetMap(uint id);
		Task<Map[]> GetMaps();

		Task<Shop> GetShopByVendorName(string vendorName);
		Task<Shop> GetFirstShop();

		Task<List<TradepostListing>> GetTradepostListings(uint itemId);
		Task<uint?> GetItemSearchCategory(uint template);
		Task<TradepostListing> GetTradepostListingBySaleId(ulong saleId);
		Task<TradepostListing> GetTradepostListingBySellerSlot(string sellerName, uint bankSlot);
		Task CreateTradepostListing(TradepostListing listing);
		Task SetTradepostListingPrice(ulong saleId, ulong price);
		Task DeleteTradepostListing(ulong saleId);

		Task<Mail> CreateMail(Mail mail);
		Task<List<Mail>> GetMailsForRecipient(string recipientName);
		Task<Mail> GetMailByWireId(ulong mailId);
		Task SetMailRead(ulong mailId, bool read);
		Task RemoveMailAttachment(ulong mailId, ulong itemInstanceId);
		Task DeleteMail(ulong mailId);

		Task<MapWorldBurst> GetMapWorldBurst(uint mapId);
		Task<Dictionary<string, MorphTarget>> GetMorphTargets(IReadOnlyCollection<string> names);
		Task<NpcSpawn> GetNpcSpawnByName(string name);
		Task<List<NpcSpell>> GetNpcSpellsByName(string name);
		Task<NpcSpawn[]> GetNpcSpawns(uint mapId);
		Task<PatrolPath[]> GetPatrolPaths(uint mapId);
		Task<int> DeleteNpcSpawns(uint mapId, string name, float? x, float? y, float? z, float radius);

		Task<Realm> GetRealm(uint id);
		Task<Realm[]> GetRealms();
		Task<RespawnPoint[]> GetRespawnPoints(uint mapId);

		// The default spawn for a map: the AlwaysKnown starter point, else the first respawn point on the map, else
		// null (the map has no points at all - callers decide whether to refuse). Replaces the old Map.Position.
		Task<RespawnPoint> GetDefaultSpawn(uint mapId);

		Task<Spell> GetSpell(uint id);
		Task<Spell[]> GetSpells();
		Task<Ability[]> GetStartingAbilities(uint level, uint @class);

		Task<uint[]> GetStartingSpells(uint level, uint @class);
		Task<WorldObject[]> GetWorldObjects(uint mapId);
		Task<T> Query<T>(Func<IDatabaseContext, Task<T>> work);
		Task RemoveCharacterQuest(uint characterId, uint questId);
		Task SaveAccount(Account account);
		Task SaveCharacter(Character character);
		Task SaveCharacterEquipment(CharacterEquipment equipment);
		Task SaveCharacterScaffold(Character character);
		Task SaveCharacterTransform(uint characterId, Vector3 position, Quaternion rotation, uint mapId);

		Task SaveCharacterBind(uint characterId, uint respawnPointId);

		Task SaveCharacterDeadState(uint characterId, bool isDead);

		Task<RespawnPoint> GetRespawnPoint(uint id);
		Task SetCharacterInventorySlot(uint characterId, int container, int slot, uint? itemId, uint count = 1);
		Task SetCharacterMoney(uint characterId, ulong money);
		Task SetCharacterProgress(uint characterId, uint experience, ushort level);
		Task SetCharacterQuest(uint characterId, uint questId, QuestState state, uint goalProgress = 0, uint goalRequired = 0, string goalTargetName = null);
		Task<int> SetCreationSpawn(Character character);

		Task<bool> UpdateLastInfo(Account account, INetworkClient client);
		Task<bool> UpdateLastInfo(Character character, INetworkClient client);
	}

	public class DatabaseRepository : IDatabaseRepository
	{
		// Character ids (which double as the on-wire client instance) are DB-generated (SQLite max+1,
		// continuing from the existing 0x0200xxxx rows). The old manual in-range allocation is gone: the
		// burst replay pads varints (EncodeVarintPadded), so any id below 2^28 substitutes length-preserving
		// into the captured packets.

		// Character ids ARE the on-wire client instance and the burst replay substitutes them into captured 4-byte
		// protobuf-varint slots, so they must encode to <= 4 varint bytes (id <= 0x0FFFFFFF). Allocate from this base
		// continuing the existing 0x013xxxxx rows; a 5-byte id (> 0x0FFFFFFF) crashes the client (see DatabaseContext).
		private const uint FirstCharacterId = 0x02000000;
		private const uint MaxCharacterId = 0x0FFFFFFF;

		private readonly IDbContextFactory<DatabaseContext> _contextFactory;

		public DatabaseRepository(IDbContextFactory<DatabaseContext> contextFactory)
		{
			_contextFactory = contextFactory;
		}

		// Sets a tag on a character (idempotent - no duplicate row).
		public async Task AddCharacterTag(uint characterId, uint tagId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			if (await ctx.CharacterTags.AnyAsync(t => t.CharacterId == characterId && t.TagId == tagId))
				return;

			ctx.CharacterTags.Add(new CharacterTag
			{
				CharacterId = characterId,
				TagId = tagId
			});
			await ctx.SaveChangesAsync();
		}

		// --- login / account -----------------------------------------------------------------------------------

		public async Task<bool> CheckLogin(string username, string password)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Accounts.AnyAsync(a => a.UserName == username && a.Password == password); // :)
		}

		public async Task<Character> CreateCharacter(uint accountId, uint realmId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();

			// Captures are authoritative: the create world-entry uses map 0xFAA. Make sure the row exists
			// so the character->Map foreign key can never fail, inserting a best-effort row if the seeded
			// Maps table is missing it.
			await EnsureMapExists(ctx, Map.TortageBeachId, "Tortage Beach");

			// Only reuse an IN-RANGE unnamed row; an out-of-range leftover (>0x0FFFFFFF, e.g. created when a high-id
			// captured char poisoned the sequence) would yield a 5-byte instance the burst substitution can't pad, so
			// drop those and allocate fresh in range.
			var character = await ctx.Characters.FirstOrDefaultAsync(c => c.AccountId == accountId && c.Name == null && c.Id <= MaxCharacterId);
			if (character == null)
			{
				var stale = await ctx
					.Characters
					.Where(c => c.AccountId == accountId && c.Name == null)
					.ToListAsync();
				if (stale.Count > 0)
				{
					ctx.Characters.RemoveRange(stale);
					await ctx.SaveChangesAsync();
				}

				// Allocate the id explicitly from the IN-RANGE max (ignore high-id captured chars), so it stays a
				// 4-byte varint (<= 0x0FFFFFFF). The id IS the on-wire client instance and the burst substitution
				// pads it into the captured 4-byte slot - a 5-byte id breaks every nested length prefix and crashes
				// the client. See DatabaseContext (Character.Id ValueGeneratedNever).
				var maxInRange = await ctx
					.Characters
					.Where(c => c.Id <= MaxCharacterId)
					.Select(c => (uint?)c.Id)
					.MaxAsync() ?? (FirstCharacterId - 1);
				var nextId = maxInRange + 1;
				if (nextId > MaxCharacterId)
					throw new InvalidOperationException($"Character id space exhausted in the 4-byte varint range (max {MaxCharacterId:X}).");

				character = new Character
				{
					Id = nextId,
					AccountId = accountId,
					RealmId = realmId,
					AuthChallenge = Guid
						.NewGuid()
						.ToString("N"),
					Level = 1,
					CreationDate = DateTime.UtcNow,
					MapId = Map.TortageBeachId,
					Position = Map.NewPlayerSpawnSpawnPoint.Position
				};

				// Seed default vitals (cascade-inserts with the character). Class isn't chosen yet at row
				// allocation, so this is the level-1 rogue-default baseline; regenerate when class is committed.
				character.MaxStats = CharacterMaxStats.CreateDefaults(character.Class, character.Level);

				ApplyCreationSpawn(character);
				ctx.Characters.Add(character);
				await ctx.SaveChangesAsync();
			}

			return character;
		}

		// Deletes a character and its dependent rows. CharacterBodies/CharacterEquipment cascade on the
		// character delete; Account.ClientInstance is no longer a foreign key, so deleting a character never
		// affects the account. Returns false if no such character existed.
		public async Task<bool> DeleteCharacter(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var character = await ctx.Characters.FindAsync(characterId);
			if (character == null)
				return false;

			ctx.Characters.Remove(character);
			await ctx.SaveChangesAsync();
			return true;
		}

		// Records that a character has discovered a respawn point (idempotent - no duplicate row).
		public async Task DiscoverRespawnPoint(uint characterId, uint respawnPointId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var exists = await ctx.CharacterRespawnPoints.AnyAsync(c => c.CharacterId == characterId && c.RespawnPointId == respawnPointId);
			if (exists)
				return;

			ctx.CharacterRespawnPoints.Add(new CharacterRespawnPoint
			{
				CharacterId = characterId,
				RespawnPointId = respawnPointId
			});
			await ctx.SaveChangesAsync();
		}

		// --- ad-hoc units of work ------------------------------------------------------------------------------

		public async Task Execute(Func<IDatabaseContext, Task> work)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await work(ctx);
		}

		// The whole ability catalog (starting kit + harvested class/higher-level abilities). Used by the GM
		// .spelllist/.grantspell commands - the only things grantable via the proven 0x2052 packet (the Spell table
		// has no grant identity quad).
		public async Task<Ability[]> GetAbilities()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.Abilities
				.OrderBy(a => a.Id)
				.ToArrayAsync();
		}

		public async Task<Ability> GetAbility(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Abilities.FirstOrDefaultAsync(a => a.Id == id);
		}

		public async Task<Account> GetAccount(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Accounts.FirstOrDefaultAsync(a => a.Id == id);
		}

		public async Task<Account> GetAccount(string userName)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Accounts.FirstOrDefaultAsync(a => a.UserName == userName);
		}

		// --- characters --------------------------------------------------------------------------------------

		public async Task<Character> GetCharacter(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			// Include Body so the create-editor can resume an existing character's morphs without creating a
			// duplicate body row, and MaxStats so vitals are available. SaveCharacter's graph stays
			// Character (+Body, +MaxStats).
			return await ctx
				.Characters
				.Include(c => c.Body)
				.Include(c => c.MaxStats)
				.FirstOrDefaultAsync(c => c.Id == id);
		}

		// Character names are unique (guild invites and GM commands address players by name).
		public async Task<Character> GetCharacterByName(string name)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Characters.FirstOrDefaultAsync(c => c.Name == name);
		}

		// --- hotbar shortcuts (persistence only; the login restore path is undecoded - see CharacterShortcut) ---

		public async Task<CharacterShortcut[]> GetCharacterShortcuts(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.CharacterShortcuts
				.Where(s => s.CharacterId == characterId)
				.OrderBy(s => s.Slot)
				.ToArrayAsync();
		}

		public async Task RemoveCharacterShortcut(uint characterId, uint slot)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx
				.CharacterShortcuts
				.Where(s => s.CharacterId == characterId && s.Slot == slot)
				.ExecuteDeleteAsync();
		}

		// Upserts a hotbar slot (the client re-sends AddShortcut for an occupied slot when it is overwritten).
		public async Task SetCharacterShortcut(uint characterId, uint slot, uint shortcutType, uint targetId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var existing = await ctx.CharacterShortcuts.FirstOrDefaultAsync(s => s.CharacterId == characterId && s.Slot == slot);
			if (existing == null)
			{
				ctx.CharacterShortcuts.Add(new CharacterShortcut
				{
					CharacterId = characterId,
					Slot = slot,
					ShortcutType = shortcutType,
					TargetId = targetId
				});
			}
			else
			{
				existing.ShortcutType = shortcutType;
				existing.TargetId = targetId;
			}

			await ctx.SaveChangesAsync();
		}

		// --- guilds ----------------------------------------------------------------------------------------

		// Creates a guild with the given character as its leader. Returns null when the name is taken or the
		// character is already in a guild.
		public async Task<Guild> CreateGuild(string name, uint leaderCharacterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			if (await ctx.Guilds.AnyAsync(g => g.Name == name))
				return null;

			var leader = await ctx.Characters.FirstOrDefaultAsync(c => c.Id == leaderCharacterId);
			if (leader == null || leader.GuildId != null)
				return null;

			var guild = new Guild
			{
				Name = name,
				CreationDate = DateTimeOffset.UtcNow
			};
			ctx.Guilds.Add(guild);
			await ctx.SaveChangesAsync();

			leader.GuildId = guild.Id;
			leader.GuildRank = Character.GuildLeaderRank;
			await ctx.SaveChangesAsync();

			return await LoadGuild(ctx, guild.Id);
		}

		public async Task<Guild> GetGuild(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await LoadGuild(ctx, id);
		}

		public async Task<Guild> GetGuildByName(string name)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var id = await ctx
				.Guilds
				.Where(g => g.Name == name)
				.Select(g => g.Id)
				.FirstOrDefaultAsync();

			return id == 0
				? null
				: await LoadGuild(ctx, id);
		}

		public async Task SaveGuild(Guild guild)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			// Detach the members navigation: only the guild row itself (MOTD/description/cash/...) is saved here;
			// membership changes go through SetCharacterGuild.
			ctx.Entry(guild).State = EntityState.Modified;
			await ctx.SaveChangesAsync();
		}

		// Sets (or clears, guildId = null) a character's guild membership. Returns false when the character does
		// not exist or the guild does not exist.
		public async Task<bool> SetCharacterGuild(uint characterId, uint? guildId, byte rank)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var character = await ctx.Characters.FirstOrDefaultAsync(c => c.Id == characterId);
			if (character == null)
				return false;

			if (guildId != null && !await ctx.Guilds.AnyAsync(g => g.Id == guildId))
				return false;

			character.GuildId = guildId;
			character.GuildRank = guildId == null
				? (byte)0
				: rank;
			await ctx.SaveChangesAsync();
			return true;
		}

		// Guild with the full member roster (the 0x203F full update needs every member's name/level/class).
		private static Task<Guild> LoadGuild(DatabaseContext ctx, uint id)
		{
			return ctx
				.Guilds
				.Include(g => g.Members)
				.FirstOrDefaultAsync(g => g.Id == id);
		}

		// --- shops -----------------------------------------------------------------------------------------

		public async Task<Shop> GetShopByVendorName(string vendorName)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.Shops
				.Include(s => s.Items)
				.FirstOrDefaultAsync(s => s.VendorName == vendorName);
		}

		public async Task<Shop> GetFirstShop()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.Shops
				.Include(s => s.Items)
				.OrderBy(s => s.Id)
				.FirstOrDefaultAsync();
		}

		// Auction-house listings for a searched item template, cheapest first (the order the grid renders). Price is
		// a ulong (tin), which SQLite can't ORDER BY in SQL, so fetch the (small) match set and sort client-side.
		public async Task<List<TradepostListing>> GetTradepostListings(uint itemId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var rows = await ctx
				.TradepostListings
				.Where(l => l.ItemId == itemId)
				.ToListAsync();
			return rows.OrderBy(l => l.Price).ToList();
		}

		// The AH search CATEGORY (the item-type-search id the client queries by, e.g. 30000 / 30030 - NOT the item
		// template) for an item template, recovered from CAPTURED listings, where ItemId is the real category and
		// Template is the per-item id. The mapping is a clean function (each template -> one category; verified
		// conflict-free across 171 captured templates); we take the most common to be safe. Returns null when no
		// captured listing covers the template (caller then falls back to the template). Issue #181.
		public async Task<uint?> GetItemSearchCategory(uint template)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();

			// Prefer the RDB-decoded category on the item template (covers EVERY item, not just captured ones).
			var item = await ctx.Items.FirstOrDefaultAsync(i => i.Id == template);
			if (item is { SearchCategory: > 0 })
				return item.SearchCategory;

			// Fallback: recover it from captured listings (the items seen in AH captures) for DBs without an RDB import.
			var categories = await ctx
				.TradepostListings
				.Where(l => l.Template == template && l.ItemId != l.Template)
				.Select(l => l.ItemId)
				.ToListAsync();
			if (categories.Count == 0)
				return null;

			return categories.GroupBy(c => c).OrderByDescending(g => g.Count()).First().Key;
		}

		// One listing by its unique sale id - the buy targets this (TradepostBuyItem [saleId]).
		public async Task<TradepostListing> GetTradepostListingBySaleId(ulong saleId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.TradepostListings.FirstOrDefaultAsync(l => l.SaleId == saleId);
		}

		// The seller's active listing for a given origin bank slot - how the seller's own change-price / cancel ops
		// address their listing (they carry nBankItemSlot, not the saleId). Keyed by (seller, slot).
		public async Task<TradepostListing> GetTradepostListingBySellerSlot(string sellerName, uint bankSlot)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.TradepostListings.FirstOrDefaultAsync(l => l.SellerName == sellerName && l.BankSlot == bankSlot);
		}

		// Creates a new listing - a player putting a bank item up for sale (TradepostSellItem).
		public async Task CreateTradepostListing(TradepostListing listing)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			ctx.TradepostListings.Add(listing);
			await ctx.SaveChangesAsync();
		}

		// Reprices a listing (TradepostChangeItemPrice).
		public async Task SetTradepostListingPrice(ulong saleId, ulong price)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.TradepostListings.Where(l => l.SaleId == saleId).ExecuteUpdateAsync(s => s.SetProperty(l => l.Price, price));
		}

		// Removes a listing once it is bought (or cancelled). No-op if it is already gone.
		public async Task DeleteTradepostListing(ulong saleId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.TradepostListings.Where(l => l.SaleId == saleId).ExecuteDeleteAsync();
		}

		// --- mail ------------------------------------------------------------------------------------------

		// Persists a new mail (with its attachments) - the buy->mail delivery creates a system mail to the buyer
		// with the bought item escrowed as an attachment. Returns the saved row (Attachments populated).
		public async Task<Mail> CreateMail(Mail mail)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			ctx.Mails.Add(mail);
			await ctx.SaveChangesAsync();
			return mail;
		}

		// All mail addressed to a character (newest first), attachments eager-loaded so the codec can build the
		// item preview/grant without a second round-trip. Recipient NAME is the stable key (the wire id is volatile).
		public async Task<List<Mail>> GetMailsForRecipient(string recipientName)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.Mails
				.Include(m => m.Attachments)
				.Where(m => m.RecipientName == recipientName)
				.OrderByDescending(m => m.Id)
				.ToListAsync();
		}

		// One mail by its wire MailId (the GetMailByID / GetMailItem target), attachments eager-loaded.
		public async Task<Mail> GetMailByWireId(ulong mailId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.Mails
				.Include(m => m.Attachments)
				.FirstOrDefaultAsync(m => m.MailId == mailId);
		}

		// Marks a mail read/unread (the footer word flips 0x0b unread -> 0x09 read on the wire).
		public async Task SetMailRead(ulong mailId, bool read)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Mails
				.Where(m => m.MailId == mailId)
				.ExecuteUpdateAsync(s => s.SetProperty(m => m.IsRead, read));
		}

		// Removes one escrowed attachment from a mail once the recipient has taken it into the bag (GetMailItem).
		public async Task RemoveMailAttachment(ulong mailId, ulong itemInstanceId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var mail = await ctx.Mails.FirstOrDefaultAsync(m => m.MailId == mailId);
			if (mail == null)
				return;
			await ctx.MailAttachments
				.Where(a => a.MailId == mail.Id && a.ItemInstanceId == itemInstanceId)
				.ExecuteDeleteAsync();
		}

		// Deletes a mail (cascades to its attachments via the FK).
		public async Task DeleteMail(ulong mailId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Mails.Where(m => m.MailId == mailId).ExecuteDeleteAsync();
		}

		// --- equipment -------------------------------------------------------------------------------------

		public async Task<CharacterEquipment> GetCharacterEquipment(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			// Eager-load every equip-slot item nav + the bag slot so the inventory builder can read them detached.
			return await ctx
				.CharacterEquipment
				.Include(e => e.Neck)
				.Include(e => e.Head)
				.Include(e => e.Tokens)
				.Include(e => e.Chest)
				.Include(e => e.MainHandLeft)
				.Include(e => e.MainHandRight)
				.Include(e => e.OffHandLeft)
				.Include(e => e.OffHandRight)
				.Include(e => e.Belt)
				.Include(e => e.RingLeft)
				.Include(e => e.RingRight)
				.Include(e => e.Cloak)
				.Include(e => e.Shoulder)
				.Include(e => e.Boots)
				.Include(e => e.Hands)
				.Include(e => e.Legs)
				.Include(e => e.FirstPosBag)
				.Include(e => e.Wrist)
				.FirstOrDefaultAsync(e => e.CharacterId == characterId);
		}

		public async Task<Character> GetCharacterForAccount(uint accountId, uint clientInstance)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();

			var directMatch = await ctx
				.Characters
				.Include(c => c.Body)
				.Include(c => c.MaxStats)
				.FirstOrDefaultAsync(c => c.AccountId == accountId && c.Id == clientInstance);
			if (directMatch != null)
				return directMatch;

			return await ctx
				.Characters
				.Include(c => c.Body)
				.Include(c => c.MaxStats)
				.Where(c => c.AccountId == accountId)
				.OrderByDescending(c => c.Id)
				.FirstOrDefaultAsync();
		}

		// The character's non-worn carried items (bag/resource/quest container rows), with the item navs for the
		// snapshot builder. Detached like every read here.
		public async Task<CharacterInventory[]> GetCharacterInventory(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.CharacterInventory
				.Include(i => i.Item)
				.Where(i => i.CharacterId == characterId)
				.ToArrayAsync();
		}

		public async Task<CharacterQuest[]> GetCharacterQuests(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.CharacterQuests
				.Where(q => q.CharacterId == characterId)
				.ToArrayAsync();
		}

		// Quest-giver NPC name -> the quest id it grants (from ConversationNodes.GrantQuestId). Drives the per-NPC
		// quest marker (!/?) - which NPC offers/turns-in which quest (#241). One quest per name (the lowest id if an
		// NPC grants several); cached by the caller.
		public async Task<Dictionary<string, uint>> GetQuestGiverMap()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var grants = await ctx.ConversationNodes
				.Where(n => n.GrantQuestId != 0 && n.NpcName != null)
				.Select(n => new { n.NpcName, n.GrantQuestId })
				.Distinct()
				.ToListAsync();
			return grants
				.GroupBy(g => g.NpcName)
				.ToDictionary(g => g.Key, g => g.Min(x => x.GrantQuestId));
		}

		// NPCs whose world spawn is GATED on a quest (#261): an NPC that is a KILL-goal target of some quest needs to
		// be present only while a player carries that quest, so it is streamed solely to holders (interest gating).
		// Quest GIVERS are excluded - they must always be visible to hand the quest out (Casilda is both a giver and a
		// later talk/escort target, so she stays ungated). Returns NpcName -> the quest ids that reveal it. Data-driven,
		// so any future quest with a kill target gets the same treatment automatically.
		public async Task<Dictionary<string, uint[]>> GetQuestGatedNpcMap()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var givers = (await ctx.ConversationNodes
					.Where(n => n.GrantQuestId != 0 && n.NpcName != null)
					.Select(n => n.NpcName)
					.Distinct()
					.ToListAsync())
				.ToHashSet();

			var killTargets = await ctx.QuestGoalDefinitions
				.Where(g => g.GoalType == QuestGoalType.Kill && g.TargetName != null && g.TargetName != "")
				.Join(ctx.QuestDefinitions, g => g.QuestDefinitionId, d => d.Id, (g, d) => new { g.TargetName, d.QuestId })
				.Distinct()
				.ToListAsync();

			return killTargets
				.Where(x => !givers.Contains(x.TargetName))
				.GroupBy(x => x.TargetName)
				.ToDictionary(g => g.Key, g => g.Select(x => x.QuestId).Distinct().ToArray());
		}

		// The spells a character has learned; GrantStartingSpells replays these as DispatchLearnSpell grants on login.
		public async Task<CharacterSpell[]> GetCharacterSpells(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.CharacterSpells
				.Where(s => s.CharacterId == characterId)
				.OrderBy(s => s.Id)
				.ToArrayAsync();
		}

		// Records that a character has learned a spell (idempotent: returns false when already known). The grant
		// PACKET (DispatchLearnSpell) is sent separately; this is the persistence so the spell survives relog.
		public async Task<bool> AddCharacterSpell(uint characterId, uint spellId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			if (await ctx.CharacterSpells.AnyAsync(s => s.CharacterId == characterId && s.SpellId == spellId))
				return false;

			ctx.CharacterSpells.Add(new CharacterSpell
			{
				CharacterId = characterId,
				SpellId = spellId
			});
			await ctx.SaveChangesAsync();
			return true;
		}

		// The shared journal-content definition for a quest (text refs + objective marker), or null if not seeded.
		public async Task<QuestDefinition> GetQuestDefinition(uint questId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.QuestDefinitions
				.Include(q => q.TextRefs)
				.Include(q => q.Goals)
				.FirstOrDefaultAsync(q => q.QuestId == questId);
		}

		public async Task<Character[]> GetCharactersByAccount(uint id, bool skipUninitialized = true)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var q = ctx.Characters.Where(a => a.AccountId == id);
			if (skipUninitialized)
				q = q.Where(c => c.Name != null);

			return await q.ToArrayAsync();
		}

		// Loads the character with its spawn-scaffold rows (entry table / feat-ID list / ending pair lists /
		// pre-IIR frame) plus Body and BodyResources, so first-entry seeding can generate the morph and
		// weighted-appearance entries from the editor choices. Detached like every read here.
		public async Task<Character> GetCharacterScaffold(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.Characters
				.Include(c => c.Attributes)
				.Include(c => c.Skills)
				.Include(c => c.ScaffoldPairs)
				.Include(c => c.SpawnFrame)
				.Include(c => c.Body)
				.Include(c => c.BodyResources)
				.FirstOrDefaultAsync(c => c.Id == characterId);
		}

		public async Task<uint[]> GetCharacterTags(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.CharacterTags
				.Where(t => t.CharacterId == characterId)
				.Select(t => t.TagId)
				.ToArrayAsync();
		}

		// One screen in an NPC's dialogue tree, keyed by the stable (NpcName, TypeId, AnswerPath). The runtime serves
		// the "" screen on the greeting click and the "path>idx" child screen on each KnubotAnswer (the answer index
		// is the real branch selector; the old Mode/Field3 were session-volatile counters). Null = no captured screen
		// for that path (an un-walked branch) -> the runtime closes the window gracefully. See memory
		// protocol_conversation_routing.
		public async Task<ConversationNode> GetConversationScreen(string npcName, uint typeId, string answerPath)
		{
			if (string.IsNullOrEmpty(npcName))
				return null;

			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.ConversationNodes
				.Include(n => n.Packets)
				.FirstOrDefaultAsync(n => n.NpcName == npcName && n.TypeId == typeId && n.AnswerPath == answerPath);
		}

		// All quest/tag-conditioned variants of a screen (same NpcName/TypeId/AnswerPath, differing ConditionScript).
		// The runtime evaluates each variant's condition against the live player and serves the matching one (the
		// unconditional "" variant is the fallback). Ordered conditioned-first so the caller can take the first pass.
		public async Task<List<ConversationNode>> GetConversationScreens(string npcName, uint typeId, string answerPath)
		{
			if (string.IsNullOrEmpty(npcName))
				return new List<ConversationNode>();

			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var nodes = await ctx
				.ConversationNodes
				.Include(n => n.Packets)
				.Where(n => n.NpcName == npcName && n.TypeId == typeId && n.AnswerPath == answerPath)
				.ToListAsync();

			// Conditioned variants first, the unconditional fallback last.
			return nodes.OrderBy(n => string.IsNullOrEmpty(n.ConditionScript)).ToList();
		}

		public async Task<uint[]> GetDiscoveredRespawnPointIds(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.CharacterRespawnPoints
				.Where(c => c.CharacterId == characterId)
				.Select(c => c.RespawnPointId)
				.ToArrayAsync();
		}

		public async Task<Item> GetItem(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Items.FirstOrDefaultAsync(i => i.Id == id);
		}

		// Items whose name contains the substring, case-insensitive (LOWER() both sides). Discovery for the
		// MCP search_items tool and .additem.
		public async Task<Item[]> SearchItems(string nameSubstring, int limit = 25)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var lowered = nameSubstring.ToLowerInvariant();
			return await ctx
				.Items
				.Where(i => i.Name != null && i.Name.ToLower().Contains(lowered))
				.OrderBy(i => i.Name)
				.Take(limit)
				.ToArrayAsync();
		}

		// A random sample of snapshot-buildable gear/normal items (Archetype != 0, so SnapshotItemBuilder can
		// render them; not a Quest or Resource item, so those don't end up as random loot). Used to fill loot bags
		// with a couple of test items (.spawnbag, NPC-kill drops). SQLite RANDOM() order; fewer than `count` come
		// back only when the item table has fewer matching rows.
		public async Task<Item[]> GetRandomBuildableItems(int count)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.Items
				.Where(i => i.Archetype != 0 && i.Name != null && i.Name != "" && i.Type == ItemType.Normal)
				.OrderBy(_ => EF.Functions.Random())
				.Take(count)
				.ToArrayAsync();
		}

		// Captured NPC spawn rows whose name contains the substring, case-insensitive, any map. Rows without
		// spawn Data are included (still useful to know the NPC exists) - callers check Data for spawnability.
		public async Task<NpcSpawn[]> SearchNpcSpawns(string nameSubstring, int limit = 50)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var lowered = nameSubstring.ToLowerInvariant();
			return await ctx
				.NpcSpawns
				.Include(n => n.Npc)
				.Where(n => n.Npc != null && n.Npc.Name != null && n.Npc.Name.ToLower().Contains(lowered)) // #244: name lives on the type
				.OrderBy(n => n.Npc.Name)
				.ThenBy(n => n.MapId)
				.Take(limit)
				.ToArrayAsync();
		}

		public async Task<LuaScript> GetLuaScript(uint scriptId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.LuaScripts.FirstOrDefaultAsync(s => s.Id == scriptId);
		}

		public async Task<Map> GetMap(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Maps.FirstOrDefaultAsync(m => m.Id == id);
		}

		public async Task<Map[]> GetMaps()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.Maps
				.OrderBy(m => m.Id)
				.ToArrayAsync();
		}

		public async Task<MapWorldBurst> GetMapWorldBurst(uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var burst = await ctx
				.MapWorldBursts
				.Include(b => b.Entities)
				.FirstOrDefaultAsync(b => b.MapId == mapId);

			// Guard: a burst with an unknown captured instance (0) can't be re-addressed, so treat it as absent and
			// let login fall back. Out-of-range instances ARE supported now - RewriteBurstInstance pads the live id's
			// varint to the captured length (overlong), keeping the substitution length-preserving.
			if (burst != null && burst.CapturedInstance == 0)
				return null;

			return burst;
		}

		// Resolves appearance resource refs ("*texscript_1108153") to their client-registry entries (imported
		// from RDB 1000076 by Faolan.Importer --rdb). Empty when the registry hasn't been imported.
		public async Task<Dictionary<string, MorphTarget>> GetMorphTargets(IReadOnlyCollection<string> names)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.MorphTargets
				.Where(t => names.Contains(t.Name))
				.ToDictionaryAsync(t => t.Name);
		}

		// The captured static world objects for a map (decor/containers/zones). The world-entry path builds each
		// object's body from its row via WorldObjectBuilder, like the NPC spawns.
		// A captured NPC spawn by name (case-insensitive), any map (used by .spawnnpc and to fetch a mount-entity
		// template like "Fading Black Horse" to spawn at the player). Prefers a row that has spawn Data.
		public async Task<NpcSpawn> GetNpcSpawnByName(string name)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var lowered = name.ToLowerInvariant();
			return await ctx
				.NpcSpawns
				.Include(n => n.Npc).ThenInclude(t => t.Spells)
				.Include(n => n.Morphs)
				.Include(n => n.Equipment)
				.Where(n => n.Npc != null && n.Npc.Name != null && n.Npc.Name.ToLower() == lowered && n.Data != null) // #244: name lives on the type
				.FirstOrDefaultAsync();
		}

		// Deletes the persisted NpcSpawn rows for a named entity on a map - the .delete GM command, for scrubbing
		// captured import artifacts (phantom players that happened to be near the capturing client in the pcap). With a
		// position, scopes to rows within `radius` of it (so deleting one faction mob doesn't wipe every same-name mob on
		// the map); without (x null), deletes EVERY same-name row on the map (a unique neutral named entity, whose
		// cross-session captures collapse to a single render). Returns the row count removed.
		public async Task<int> DeleteNpcSpawns(uint mapId, string name, float? x, float? y, float? z, float radius)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var lowered = (name ?? "").ToLowerInvariant();
			var query = ctx
				.NpcSpawns
				.Where(n => n.MapId == mapId && n.Npc != null && n.Npc.Name != null && n.Npc.Name.ToLower() == lowered); // #244: name lives on the type
			if (x is { } px && y is { } py && z is { } pz)
				query = query.Where(n => n.PositionX >= px - radius && n.PositionX <= px + radius
					&& n.PositionY >= py - radius && n.PositionY <= py + radius
					&& n.PositionZ >= pz - radius && n.PositionZ <= pz + radius);
			return await query.ExecuteDeleteAsync();
		}

		// The spells a caster NPC casts (keyed by its stable name), for the sim's NPC spell-casting (#232).
		public async Task<List<NpcSpell>> GetNpcSpellsByName(string name)
		{
			if (string.IsNullOrEmpty(name))
				return [];
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var lowered = name.ToLowerInvariant();
			return await ctx
				.NpcSpells
				.Where(s => s.NpcName != null && s.NpcName.ToLower() == lowered)
				.ToListAsync();
		}

		// The captured NPC entity spawns for a map (FA015807 bodies in Data + the lifted identity/transform
		// columns). The world-entry path builds each NPC's spawn from its row via NpcSpawnBuilder.
		public async Task<NpcSpawn[]> GetNpcSpawns(uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.NpcSpawns
				.Include(n => n.Morphs)
				.Include(n => n.Equipment)
				.Include(n => n.Npc).ThenInclude(t => t.Spells) // #244: the NPC type + its FK'd spells (replaces the name-string spell join)
				.Where(n => n.MapId == mapId && n.Data != null)
				.ToArrayAsync();
		}

		// A map's captured patrol routes (#266 patrol), each with its ordered waypoints. Keyed at the paired NPC's
		// spawn position (the importer correlates route->spawn by handle and stores the route at the spawn point), so
		// the world model links a route to its NPC by exact position.
		public async Task<PatrolPath[]> GetPatrolPaths(uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.PatrolPaths
				.Include(p => p.Waypoints)
				.Where(p => p.MapId == mapId)
				.ToArrayAsync();
		}

		// --- reference data --------------------------------------------------------------------------------

		public async Task<Realm> GetRealm(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Realms.FirstOrDefaultAsync(r => r.Id == id);
		}

		public async Task<Realm[]> GetRealms()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.Realms
				.OrderBy(r => r.Id)
				.ToArrayAsync();
		}

		public async Task<RespawnPoint[]> GetRespawnPoints(uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.RespawnPoints
				.Where(r => r.MapId == mapId)
				.ToArrayAsync();
		}

		public async Task<RespawnPoint> GetDefaultSpawn(uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var points = ctx.RespawnPoints.Where(r => r.MapId == mapId);
			// AlwaysKnown starter wins; otherwise any point on the map; null if the map defines none.
			return await points.FirstOrDefaultAsync(r => r.AlwaysKnown)
				?? await points.FirstOrDefaultAsync();
		}

		public async Task<Spell> GetSpell(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Spells.FirstOrDefaultAsync(s => s.Id == id);
		}

		public async Task<Spell[]> GetSpells()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.Spells
				.OrderBy(s => s.Id)
				.ToArrayAsync();
		}

		// The universal starting ability kit (IsStartingAbility) - the abilities every new character receives via
		// 0x2052 during creation (3 Normal Attack directions + toggle-combat / switch-weapon / utility), imported
		// from the retail create-flow grants by CaptureAbilityImporter. The `level` is reserved for future
		// level-gating; today the whole starting kit is level 1. The catalog also holds higher-level / class
		// abilities (IsStartingAbility = false) which are NOT granted here.
		// Starting abilities for a character: the generic kit (Class == 0, granted to everyone) plus the abilities
		// specific to the character's own class, up to its level. Class-specific coverage is whatever the captures
		// supplied (incomplete by design); the generic kit is always present.
		public async Task<Ability[]> GetStartingAbilities(uint level, uint @class)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.Abilities
				.Where(a => a.IsStartingAbility && a.Level <= level && (a.Class == 0 || a.Class == @class))
				.OrderBy(a => a.Slot)
				.ToArrayAsync();
		}

		// Starting SPELLS for a character (#74): the generic set (Class == 0) plus the spells specific to the
		// character's own class, up to its level. Harvested from the create-flow captures (ClassStartingSpell);
		// coverage is whatever the captures supplied (casters we have a beach capture for - physical classes have
		// none at L1, which is correct). The spell analogue of GetStartingAbilities.
		public async Task<uint[]> GetStartingSpells(uint level, uint @class)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.ClassStartingSpells
				.Where(s => s.Level <= level && (s.Class == 0 || s.Class == @class))
				.OrderBy(s => s.Id)
				.Select(s => s.SpellId)
				.ToArrayAsync();
		}

		public async Task<WorldObject[]> GetWorldObjects(uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.WorldObjects
				.Where(o => o.MapId == mapId && o.Data != null)
				.ToArrayAsync();
		}

		public async Task<T> Query<T>(Func<IDatabaseContext, Task<T>> work)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await work(ctx);
		}

		public async Task RemoveCharacterQuest(uint characterId, uint questId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var row = await ctx.CharacterQuests.FirstOrDefaultAsync(q => q.CharacterId == characterId && q.QuestId == questId);
			if (row == null)
				return;

			ctx.CharacterQuests.Remove(row);
			await ctx.SaveChangesAsync();
		}

		// Persists the per-session mutable Account scalars (auth status + the on-wire client instance, which the
		// separate GameServer connection reads back from a freshly-loaded account). Loads the tracked row and copies
		// just those fields, so navigation state on the detached in-memory account never leaks into the write.
		public async Task SaveAccount(Account account)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var tracked = await ctx.Accounts.FirstOrDefaultAsync(a => a.Id == account.Id);
			if (tracked == null)
				return;

			tracked.AuthStatus = account.AuthStatus;
			tracked.ClientInstance = account.ClientInstance;
			await ctx.SaveChangesAsync();
		}

		// Persists a (detached) character graph - the character row plus its Body if present - as a single unit of
		// work. Used for the create-editor's accumulated edits (sex/race/morphs/name) and in-world character edits.
		public async Task SaveCharacter(Character character)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			ctx.Characters.Update(character); // TrackGraph: existing keys -> Modified, default keys (new Body) -> Added
			await ctx.SaveChangesAsync();
		}

		public async Task SaveCharacterEquipment(CharacterEquipment equipment)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();

			// Load (or create) the tracked row and copy each slot's item by id onto it. Setting a navigation on a
			// TRACKED entity runs EF's relationship fixup so the FK column updates correctly - INCLUDING being
			// cleared to NULL when a slot is emptied (Attach/Update of a detached graph does NOT reliably null a
			// reference-nav FK, which is what silently lost the unequip/clear-bag writes).
			var tracked = await LoadTrackedEquipment(ctx, equipment.Id) ?? AddNewEquipment(ctx, equipment.Id, equipment.CharacterId);

			tracked.Neck = await ResolveItem(ctx, equipment.Neck?.Id);
			tracked.Head = await ResolveItem(ctx, equipment.Head?.Id);
			tracked.Tokens = await ResolveItem(ctx, equipment.Tokens?.Id);
			tracked.Chest = await ResolveItem(ctx, equipment.Chest?.Id);
			tracked.MainHandLeft = await ResolveItem(ctx, equipment.MainHandLeft?.Id);
			tracked.MainHandRight = await ResolveItem(ctx, equipment.MainHandRight?.Id);
			tracked.OffHandLeft = await ResolveItem(ctx, equipment.OffHandLeft?.Id);
			tracked.OffHandRight = await ResolveItem(ctx, equipment.OffHandRight?.Id);
			tracked.Belt = await ResolveItem(ctx, equipment.Belt?.Id);
			tracked.RingLeft = await ResolveItem(ctx, equipment.RingLeft?.Id);
			tracked.RingRight = await ResolveItem(ctx, equipment.RingRight?.Id);
			tracked.Cloak = await ResolveItem(ctx, equipment.Cloak?.Id);
			tracked.Shoulder = await ResolveItem(ctx, equipment.Shoulder?.Id);
			tracked.Boots = await ResolveItem(ctx, equipment.Boots?.Id);
			tracked.Hands = await ResolveItem(ctx, equipment.Hands?.Id);
			tracked.Legs = await ResolveItem(ctx, equipment.Legs?.Id);
			tracked.FirstPosBag = await ResolveItem(ctx, equipment.FirstPosBag?.Id);
			tracked.Wrist = await ResolveItem(ctx, equipment.Wrist?.Id);

			await ctx.SaveChangesAsync();
		}

		// Replaces the character's spawn-scaffold rows with the given detached ones (delete-and-insert; the rows
		// are small and only rewritten on extraction/seeding, never per-login).
		public async Task SaveCharacterScaffold(Character character)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();

			ctx.CharacterAttributes.RemoveRange(ctx.CharacterAttributes.Where(a => a.CharacterId == character.Id));
			ctx.CharacterSkills.RemoveRange(ctx.CharacterSkills.Where(s => s.CharacterId == character.Id));
			ctx.CharacterScaffoldPairs.RemoveRange(ctx.CharacterScaffoldPairs.Where(p => p.CharacterId == character.Id));
			ctx.CharacterSpawnFrames.RemoveRange(ctx.CharacterSpawnFrames.Where(f => f.CharacterId == character.Id));

			if (character.Attributes != null)
				ctx.CharacterAttributes.AddRange(character.Attributes);
			if (character.Skills != null)
				ctx.CharacterSkills.AddRange(character.Skills);
			if (character.ScaffoldPairs != null)
				ctx.CharacterScaffoldPairs.AddRange(character.ScaffoldPairs);
			if (character.SpawnFrame != null)
				ctx.CharacterSpawnFrames.Add(character.SpawnFrame);

			await ctx.SaveChangesAsync();
		}

		// Upserts (itemId set) or clears (itemId null) ONE container cell - the persistence unit of the client's
		// inventory moves (equip consumes the source cell, unequip parks into the target cell).
		public async Task SetCharacterInventorySlot(uint characterId, int container, int slot, uint? itemId, uint count = 1)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var row = await ctx.CharacterInventory.FirstOrDefaultAsync(i => i.CharacterId == characterId && i.Container == container && i.Slot == slot);

			if (itemId == null)
			{
				if (row == null)
					return;

				ctx.CharacterInventory.Remove(row);
			}
			else if (row == null)
			{
				ctx.CharacterInventory.Add(new CharacterInventory
				{
					CharacterId = characterId,
					Container = container,
					Slot = slot,
					ItemId = itemId,
					Count = count
				});
			}
			else
			{
				row.ItemId = itemId;
				row.Count = count;
			}

			await ctx.SaveChangesAsync();
		}

		// Targeted scalar flushes (Money / Transform / Progress). Each is a single bulk UPDATE (ExecuteUpdateAsync):
		// no entity load, no change-tracker, no navigation walk - so unlike the full-graph SaveCharacter they can't be
		// rolled back by a navigation conflict and don't rewrite the whole character graph. Use these for the hot-path
		// mutations (wallet, position/map, XP) that change constantly; SaveCharacter is reserved for the create-flow
		// graph-add (name + Body + MaxStats as one unit of work). Contexts are short-lived per-unit-of-work, so the
		// caller's in-memory Character is the source of truth and ExecuteUpdate not syncing it is a non-issue.

		// Persists the wallet the moment it changes (vendor/loot/.money/AH), so it survives relog without the
		// full-graph SaveCharacter. Caller updates Character.Money in memory first and passes the new total.
		public async Task SetCharacterMoney(uint characterId, ulong money)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Characters
				.Where(c => c.Id == characterId)
				.ExecuteUpdateAsync(s => s.SetProperty(c => c.Money, money));
		}

		// Persists the character's authoritative live transform (the WorldEntity's position/rotation/map) on
		// teleport / disconnect / autosave - movement isn't otherwise saved, so without this a relog loses where the
		// player was.
		public async Task SaveCharacterTransform(uint characterId, Vector3 position, Quaternion rotation, uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Characters
				.Where(c => c.Id == characterId)
				.ExecuteUpdateAsync(s => s
					.SetProperty(c => c.PositionX, position.X)
					.SetProperty(c => c.PositionY, position.Y)
					.SetProperty(c => c.PositionZ, position.Z)
					.SetProperty(c => c.RotationX, rotation.X)
					.SetProperty(c => c.RotationY, rotation.Y)
					.SetProperty(c => c.RotationZ, rotation.Z)
					.SetProperty(c => c.RotationW, rotation.W)
					.SetProperty(c => c.MapId, mapId));
		}

		// Persists a character's bind / recall point (the discovered RespawnPoint the Totem of Origin returns to) the
		// moment it's set, so it survives relog independently of the full-graph save. See issue #64.
		public async Task SaveCharacterBind(uint characterId, uint respawnPointId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Characters
				.Where(c => c.Id == characterId)
				.ExecuteUpdateAsync(s => s
					.SetProperty(c => c.BindRespawnPointId, respawnPointId));
		}

		// Persists a character's DEAD state (#214) the moment it changes - on death (true) and respawn (false) - so a
		// player who logs out dead returns dead and the login snapshot carries current-health = 0. Independent of the
		// full-graph save, like SaveCharacterBind/SetCharacterProgress.
		public async Task SaveCharacterDeadState(uint characterId, bool isDead)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Characters
				.Where(c => c.Id == characterId)
				.ExecuteUpdateAsync(s => s
					.SetProperty(c => c.IsDead, isDead));
		}

		// A single resurrection point by id (resolves a character's bind point to its map+position on recall).
		public async Task<RespawnPoint> GetRespawnPoint(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.RespawnPoints.FirstOrDefaultAsync(p => p.Id == id);
		}

		// Persists a character's levelling progress (within-level Experience + Level) the moment it changes, so XP and
		// levels survive relog without depending on the full-graph SaveCharacter at disconnect/autosave.
		public async Task SetCharacterProgress(uint characterId, uint experience, ushort level)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Characters
				.Where(c => c.Id == characterId)
				.ExecuteUpdateAsync(s => s
					.SetProperty(c => c.Experience, experience)
					.SetProperty(c => c.Level, level));
		}

		// Upserts a character's quest state/progress.
		public async Task SetCharacterQuest(uint characterId, uint questId, QuestState state, uint goalProgress = 0, uint goalRequired = 0, string goalTargetName = null)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var row = await ctx.CharacterQuests.FirstOrDefaultAsync(q => q.CharacterId == characterId && q.QuestId == questId);
			if (row == null)
			{
				ctx.CharacterQuests.Add(new CharacterQuest
				{
					CharacterId = characterId,
					QuestId = questId,
					State = state,
					GoalProgress = goalProgress,
					GoalRequired = goalRequired,
					GoalTargetName = goalTargetName
				});
			}
			else
			{
				row.State = state;
				row.GoalProgress = goalProgress;
				row.GoalRequired = goalRequired;
				row.GoalTargetName = goalTargetName;
			}

			await ctx.SaveChangesAsync();
		}

		public Task<int> SetCreationSpawn(Character character)
		{
			character.MapId = Map.TortageBeachId;
			ApplyCreationSpawn(character);
			return SaveCharacterReturningRows(character);
		}

		public async Task<bool> UpdateLastInfo(Account account, INetworkClient client)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var tracked = await ctx.Accounts.FirstOrDefaultAsync(a => a.Id == account.Id);
			if (tracked == null)
				return false;

			tracked.LastConnection = DateTime.UtcNow;
			tracked.LastIpAddress = client.IpAddress;
			account.LastConnection = tracked.LastConnection; // keep the in-memory copy in sync
			account.LastIpAddress = tracked.LastIpAddress;
			return await ctx.SaveChangesAsync() > 0;
		}

		public async Task<bool> UpdateLastInfo(Character character, INetworkClient client)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var tracked = await ctx.Characters.FirstOrDefaultAsync(c => c.Id == character.Id);
			if (tracked == null)
				return false;

			tracked.LastConnection = DateTime.UtcNow;
			tracked.LastIpAddress = client.IpAddress;
			character.LastConnection = tracked.LastConnection;
			character.LastIpAddress = tracked.LastIpAddress;
			return await ctx.SaveChangesAsync() > 0;
		}

		private async Task<int> SaveCharacterReturningRows(Character character)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			ctx.Characters.Update(character);
			return await ctx.SaveChangesAsync();
		}

		private static CharacterEquipment AddNewEquipment(DatabaseContext ctx, uint id, uint characterId)
		{
			var equipment = new CharacterEquipment
			{
				Id = id,
				CharacterId = characterId
			};
			ctx.CharacterEquipment.Add(equipment);
			return equipment;
		}

		// --- helpers --------------------------------------------------------------------------------------

		private static void ApplyCreationSpawn(Character character)
		{
			character.Position = Map.NewPlayerSpawnSpawnPoint.Position;
		}

		// Inserts a Map row only if its id is absent (INSERT OR IGNORE keeps an existing seeded row, e.g.
		// 4010 "Tortage Beach", untouched). Used so capture-referenced maps that aren't seeded still
		// satisfy the foreign key.
		private static Task EnsureMapExists(DatabaseContext ctx, uint mapId, string name)
		{
			// A Map is Id + Name + the instancing columns (#268). The instancing columns are NOT NULL and - under the
			// EnsureCreated schema (tests) - carry no SQL default, so they MUST be listed here: an INSERT OR IGNORE that
			// omitted them would be silently dropped (NOT NULL violation -> ignored), leaving no Map row and failing the
			// character->Map foreign key. 0/0 = the Shared, uncapped default (this best-effort fallback beach row).
			return ctx.Database.ExecuteSqlRawAsync(
				"INSERT OR IGNORE INTO Maps (Id, Name, InstanceMode, PopulationCap) VALUES ({0}, {1}, 0, 0)", mapId, name);
		}

		private static Task<CharacterEquipment> LoadTrackedEquipment(DatabaseContext ctx, uint id)
		{
			return ctx
				.CharacterEquipment
				.Include(e => e.Neck)
				.Include(e => e.Head)
				.Include(e => e.Tokens)
				.Include(e => e.Chest)
				.Include(e => e.MainHandLeft)
				.Include(e => e.MainHandRight)
				.Include(e => e.OffHandLeft)
				.Include(e => e.OffHandRight)
				.Include(e => e.Belt)
				.Include(e => e.RingLeft)
				.Include(e => e.RingRight)
				.Include(e => e.Cloak)
				.Include(e => e.Shoulder)
				.Include(e => e.Boots)
				.Include(e => e.Hands)
				.Include(e => e.Legs)
				.Include(e => e.FirstPosBag)
				.Include(e => e.Wrist)
				.FirstOrDefaultAsync(e => e.Id == id);
		}

		private static async Task<Item> ResolveItem(DatabaseContext ctx, uint? id)
		{
			return id == null
				? null
				: await ctx.Items.FindAsync(id.Value);
		}
	}
}
