using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Repositories
{
	public interface IDatabaseRepository // TODO: Split into multiple
	{
		Task<bool> AddCharacterSpell(uint characterId, uint spellId);
		Task AddCharacterTag(uint characterId, uint tagId);
		Task<bool> CheckLogin(string username, string password);
		Task<Character> CreateCharacter(uint accountId, uint realmId);
		Task<Guild> CreateGuild(string name, uint leaderCharacterId);
		Task<Mail> CreateMail(Mail mail);
		Task CreateTradepostListing(TradepostListing listing);
		Task<bool> DeleteCharacter(uint characterId);
		Task DeleteMail(ulong mailId);
		Task<int> DeleteNpcSpawns(uint mapId, string name, float? x, float? y, float? z, float radius);
		Task DeleteTradepostListing(ulong saleId);
		Task DiscoverRespawnPoint(uint characterId, uint respawnPointId);
		Task<int> EnsureCharacterSkills(uint characterId, IEnumerable<uint> skillIds);
		Task Execute(Func<IDatabaseContext, Task> work);
		Task<Ability[]> GetAbilities();
		Task<Ability> GetAbility(uint id);
		Task<Account> GetAccount(uint id);
		Task<Account> GetAccount(string userName);
		Task<Character> GetCharacter(uint id);
		Task<CharacterAbility[]> GetCharacterAbilities(uint characterId);
		Task<Character> GetCharacterByName(string name, uint realmId);
		Task<CharacterCombo[]> GetCharacterCombos(uint characterId);
		Task<CharacterEquipment> GetCharacterEquipment(uint characterId);
		Task<Character> GetCharacterForAccount(uint accountId, uint clientInstance);
		Task<CharacterInventory[]> GetCharacterInventory(uint characterId);
		Task<CharacterQuest[]> GetCharacterQuests(uint characterId);
		Task<Character[]> GetCharactersByAccount(uint id, bool skipUninitialized = true);
		Task<Character> GetCharacterScaffold(uint characterId);
		Task<CharacterShortcut[]> GetCharacterShortcuts(uint characterId);
		Task<CharacterSpell[]> GetCharacterSpells(uint characterId);
		Task<CharacterStat[]> GetCharacterStats(uint characterId);
		Task<uint[]> GetCharacterTags(uint characterId);
		Task<Ability[]> GetClassAbilities(uint level, Class @class);
		Task<Ability[]> GetClassAbilitiesUnlockedBetween(Class @class, uint fromLevelExclusive, uint toLevelInclusive);
		Task<Combo[]> GetClassCombos(uint level, Class @class);
		Task<Combo[]> GetClassCombosUnlockedBetween(Class @class, uint fromLevelExclusive, uint toLevelInclusive);
		Task<uint[]> GetClassSpellsUnlockedBetween(Class @class, uint fromLevelExclusive, uint toLevelInclusive);
		Task<ConversationNode> GetConversationScreen(string npcName, uint typeId, string answerPath);
		Task<List<ConversationNode>> GetConversationScreens(string npcName, uint typeId, string answerPath);
		Task<RespawnPoint> GetDefaultSpawn(uint mapId);
		Task<uint[]> GetDiscoveredRespawnPointIds(uint characterId);
		Task<Guild> GetGuild(uint id);
		Task<Guild> GetGuildByName(string name, uint realmId);
		Task<Item> GetItem(uint id);
		Task<uint?> GetItemSearchCategory(uint template);
		Task<LuaScript> GetLuaScript(uint scriptId);
		Task<Mail> GetMailByWireId(ulong mailId);
		Task<List<Mail>> GetMailsForRecipient(string recipientName, uint realmId);
		Task<Map> GetMap(uint id);
		Task<Item[]> GetMapLootSample(uint mapId, int count);
		Task<Map[]> GetMaps();
		Task<Dictionary<string, MorphTarget>> GetMorphTargets(IReadOnlyCollection<string> names);
		Task<Npc> GetNpcByName(string name);
		Task<NpcSpawn> GetNpcSpawnByName(string name);
		Task<NpcSpawn[]> GetNpcSpawns(uint mapId);
		Task<List<NpcSpell>> GetNpcSpellsByName(string name);
		Task<PatrolPath[]> GetPatrolPaths(uint mapId);
		Task<QuestDefinition> GetQuestDefinition(uint questId);
		Task<Dictionary<string, uint[]>> GetQuestGatedNpcMap();
		Task<Dictionary<string, uint>> GetQuestGiverMap();
		Task<Item[]> GetRandomBuildableItems(int count);
		Task<Item[]> GetRandomLootExcludedItems();
		Task<Realm> GetRealm(uint id);
		Task<Realm[]> GetRealms();
		Task SetRealmOnlineStatus(uint realmId, RealmOnlineStatus status);
		Task<ChatMessage[]> GetRecentChatMessages(int limit = 100, uint? channelId = null);
		Task<Recipe[]> GetRecipes();
		Task<RespawnPoint> GetRespawnPoint(uint id);
		Task<RespawnPoint[]> GetRespawnPoints(uint mapId);
		Task<Shop> GetShopByVendorName(string vendorName);
		Task<Spell> GetSpell(uint id);
		Task<Spell[]> GetSpells();
		Task<uint[]> GetStartingBarSpells(uint level, Class @class);
		Task<uint[]> GetStartingSpells(uint level, Class @class);
		Task<Dictionary<string, byte[]>> GetStockPhrases();
		Task<TradepostListing> GetTradepostListingBySaleId(ulong saleId);
		Task<TradepostListing> GetTradepostListingBySellerSlot(string sellerName, uint bankSlot, uint realmId);
		Task<List<TradepostListing>> GetTradepostListings(uint itemId, uint realmId);
		Task<WorldObject[]> GetWorldObjects(uint mapId);
		Task<T> Query<T>(Func<IDatabaseContext, Task<T>> work);
		Task RemoveCharacterQuest(uint characterId, uint questId);
		Task RemoveCharacterShortcut(uint characterId, uint slot);
		Task RemoveMailAttachment(ulong mailId, ulong itemInstanceId);
		Task SaveAccount(Account account);
		Task SaveCharacter(Character character);
		Task SaveCharacterBind(uint characterId, uint respawnPointId);
		Task SaveCharacterDeadState(uint characterId, bool isDead);
		Task SaveCharacterEquipment(CharacterEquipment equipment);
		Task SaveCharacterScaffold(Character character);
		Task SaveCharacterTransform(uint characterId, Vector3 position, Quaternion rotation, uint mapId);
		Task SaveChatMessage(ChatMessage message);
		Task SaveGuild(Guild guild);
		Task<Item[]> SearchItems(string nameSubstring, int limit = 25);
		Task<NpcSpawn[]> SearchNpcSpawns(string nameSubstring, int limit = 50);
		Task<bool> SetAccountPassword(uint accountId, string password);
		Task<bool> SetAccountState(uint accountId, AccountState state);
		Task<bool> SetCharacterGuild(uint characterId, uint? guildId, byte rank);
		Task SetCharacterInventorySlot(uint characterId, int container, int slot, uint? itemId, uint count = 1);
		Task SetCharacterMoney(uint characterId, ulong money);
		Task SetCharacterProgress(uint characterId, uint experience, ushort level);
		Task SetCharacterQuest(uint characterId, uint questId, QuestState state, uint goalProgress = 0, uint goalRequired = 0, string goalTargetName = null);
		Task SetCharacterShortcut(uint characterId, uint slot, uint shortcutType, uint targetId);
		Task<int> SetCreationSpawn(Character character);
		Task<bool> SetItemRandomLootExcluded(uint itemId, bool excluded);
		Task SetMailMoney(ulong mailId, ulong money);
		Task SetMailRead(ulong mailId, bool read);
		Task SetTradepostListingPrice(ulong saleId, ulong price);
		Task<bool> UpdateLastInfo(Account account, string ipAddress);
		Task<bool> UpdateLastInfo(Character character, string ipAddress);
	}

	public class DatabaseRepository : IDatabaseRepository
	{
		// Character ids ARE the on-wire client instance and the burst replay substitutes them into captured 4-byte
		// protobuf-varint slots, so they must encode to <= 4 varint bytes (id <= 0x0FFFFFFF). Allocate from this base
		// continuing the existing 0x013xxxxx rows; a 5-byte id (> 0x0FFFFFFF) crashes the client (see DatabaseContext).
		private const uint FirstCharacterId = 0x02000000;
		private const uint MaxCharacterId = 0x0FFFFFFF;

		// Interior lowercase words allowed inside a combo DISPLAY name (e.g. "Burn to Death", "Body and Spirit Wrack").
		private static readonly HashSet<string> ComboNameConnectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"to",
			"of",
			"and",
			"the",
			"for",
			"a",
			"in",
			"on",
			"from",
			"with",
			"your",
			"our",
			"my"
		};

		private readonly IDbContextFactory<DatabaseContext> _contextFactory;

		public DatabaseRepository(IDbContextFactory<DatabaseContext> contextFactory)
		{
			_contextFactory = contextFactory;
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
				var stale = await ctx.Characters.Where(c => c.AccountId == accountId && c.Name == null).ToListAsync();
				if (stale.Count > 0)
				{
					ctx.Characters.RemoveRange(stale);
					await ctx.SaveChangesAsync();
				}

				// Allocate the id explicitly from the IN-RANGE max (ignore high-id captured chars), so it stays a
				// 4-byte varint (<= 0x0FFFFFFF). The id IS the on-wire client instance and the burst substitution
				// pads it into the captured 4-byte slot - a 5-byte id breaks every nested length prefix and crashes
				// the client. See DatabaseContext (Character.Id ValueGeneratedNever).
				var maxInRange = await ctx.Characters.Where(c => c.Id <= MaxCharacterId).Select(c => (uint?)c.Id).MaxAsync() ?? FirstCharacterId - 1;
				var nextId = maxInRange + 1;
				if (nextId > MaxCharacterId)
					throw new InvalidOperationException($"Character id space exhausted in the 4-byte varint range (max {MaxCharacterId:X}).");

				character = new Character
				{
					Id = nextId,
					AccountId = accountId,
					RealmId = realmId,
					AuthChallenge = Guid.NewGuid().ToString("N"),
					Level = 1,
					// The real Class/Race are committed later in the editor, but the columns are enum-constrained (no
					// "unchosen" value exists), so seed a valid placeholder - the rogue hash the baseline MaxStats
					// below already assumes - overwritten when the player commits their choice.
					Class = Class.Barbarian,
					Race = Race.Cimmerian,
					CreationDate = DateTime.UtcNow,
					MapId = Map.TortageBeachId,
					Position = Map.NewPlayerSpawnSpawnPoint.Position
				};

				// Seed default vitals (cascade-inserts with the character). Class isn't committed yet at row
				// allocation, so this is the level-1 rogue-default baseline; regenerate when class is committed.
				character.MaxStats = CharacterMaxStats.CreateDefaults(character.Class, character.Level);

				ApplyCreationSpawn(character);
				ctx.Characters.Add(character);
				await ctx.SaveChangesAsync();
			}

			return character;
		}

		// Creates a guild with the given character as its leader. Returns null when the name is taken or the
		// character is already in a guild.
		public async Task<Guild> CreateGuild(string name, uint leaderCharacterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var leader = await ctx.Characters.FirstOrDefaultAsync(c => c.Id == leaderCharacterId);
			if (leader == null || leader.GuildId != null)
				return null;

			// #324: guild names are unique per REALM (the founder's), not globally.
			if (await ctx.Guilds.AnyAsync(g => g.RealmId == leader.RealmId && g.Name == name))
				return null;

			var guild = new Guild
			{
				Name = name,
				RealmId = leader.RealmId,
				CreationDate = DateTimeOffset.UtcNow
			};
			ctx.Guilds.Add(guild);
			await ctx.SaveChangesAsync();

			leader.GuildId = guild.Id;
			leader.GuildRank = Character.GuildLeaderRank;
			await ctx.SaveChangesAsync();

			return await LoadGuild(ctx, guild.Id);
		}

		// Persists a new mail (with its attachments) - the buy->mail delivery creates a system mail to the buyer
		// with the bought item escrowed as an attachment. Returns the saved row (Attachments populated).
		public async Task<Mail> CreateMail(Mail mail)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			ctx.Mails.Add(mail);
			await ctx.SaveChangesAsync();
			return mail;
		}

		// Creates a new listing - a player putting a bank item up for sale (TradepostSellItem).
		public async Task CreateTradepostListing(TradepostListing listing)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			ctx.TradepostListings.Add(listing);
			await ctx.SaveChangesAsync();
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

		// Deletes a mail (cascades to its attachments via the FK).
		public async Task DeleteMail(ulong mailId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Mails.Where(m => m.MailId == mailId).ExecuteDeleteAsync();
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
			var query = ctx.NpcSpawns.Where(n => n.MapId == mapId && n.Npc != null && n.Npc.Name != null && n.Npc.Name.ToLower() == lowered); // #244: name lives on the type
			if (x is { } px && y is { } py && z is { } pz)
			{
				query = query.Where(n =>
					n.PositionX >= px - radius && n.PositionX <= px + radius && n.PositionY >= py - radius && n.PositionY <= py + radius && n.PositionZ >= pz - radius && n.PositionZ <= pz + radius);
			}

			return await query.ExecuteDeleteAsync();
		}

		// Removes a listing once it is bought (or cancelled). No-op if it is already gone.
		public async Task DeleteTradepostListing(ulong saleId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.TradepostListings.Where(l => l.SaleId == saleId).ExecuteDeleteAsync();
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

		// Ensures the character's spawn scaffold (CharacterSkills) contains every id in `skillIds` (rank 0 for any
		// added), idempotent. Used on level-up to grant the class feat scaffold so learned spells become castable
		// (the client gates spell usability on feat presence). Returns the number of newly-added skill rows.
		public async Task<int> EnsureCharacterSkills(uint characterId, IEnumerable<uint> skillIds)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var existing = await ctx.CharacterSkills.Where(s => s.CharacterId == characterId).Select(s => s.SkillId).ToListAsync();
			var have = existing.ToHashSet();
			var added = 0;
			foreach (var id in skillIds)
			{
				if (!have.Add(id))
					continue;

				ctx.CharacterSkills.Add(new CharacterSkill
				{
					CharacterId = characterId,
					SkillId = id,
					Rank = 0
				});
				added++;
			}

			if (added > 0)
				await ctx.SaveChangesAsync();
			return added;
		}

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
			return await ctx.Abilities.OrderBy(a => a.Id).ToArrayAsync();
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

		public async Task<Character> GetCharacter(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			// Include Body so the create-editor can resume an existing character's morphs without creating a
			// duplicate body row, and MaxStats so vitals are available. SaveCharacter's graph stays
			// Character (+Body, +MaxStats).
			return await ctx.Characters.Include(c => c.Body).Include(c => c.MaxStats).FirstOrDefaultAsync(c => c.Id == id);
		}

		// The character's granted abilities (CharacterAbility) - the authoritative per-character ability set, re-granted
		// on login by GrantCharacterAbilities. Empty for a freshly-created character (the class-starting set is used).
		public async Task<CharacterAbility[]> GetCharacterAbilities(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterAbilities.Where(a => a.CharacterId == characterId).OrderBy(a => a.AbilityId).ToArrayAsync();
		}

		// Character names are unique (guild invites and GM commands address players by name).
		// #324: character names are unique per realm, so a by-name lookup is realm-scoped (the caller passes its own
		// realm - all name-addressed interactions are same-realm: guild invite, mail, GM in the realm process).
		public async Task<Character> GetCharacterByName(string name, uint realmId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Characters.FirstOrDefaultAsync(c => c.RealmId == realmId && c.Name == name);
		}

		// The character's combo book (CharacterCombo) - re-emitted on login by GrantCharacterCombos (a max-level
		// imported character never dings, so this is how it keeps its combos).
		public async Task<CharacterCombo[]> GetCharacterCombos(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterCombos.Where(c => c.CharacterId == characterId).OrderBy(c => c.ComboId).ToArrayAsync();
		}

		public async Task<CharacterEquipment> GetCharacterEquipment(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			// Eager-load every equip-slot item nav + the bag slot so the inventory builder can read them detached.
			return await ctx
				.CharacterEquipment.Include(e => e.Neck)
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

			var directMatch = await ctx.Characters.Include(c => c.Body).Include(c => c.MaxStats).FirstOrDefaultAsync(c => c.AccountId == accountId && c.Id == clientInstance);
			if (directMatch != null)
				return directMatch;

			return await ctx.Characters.Include(c => c.Body).Include(c => c.MaxStats).Where(c => c.AccountId == accountId).OrderByDescending(c => c.Id).FirstOrDefaultAsync();
		}

		// The character's non-worn carried items (bag/resource/quest container rows), with the item navs for the
		// snapshot builder. Detached like every read here.
		public async Task<CharacterInventory[]> GetCharacterInventory(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterInventory.Include(i => i.Item).Where(i => i.CharacterId == characterId).ToArrayAsync();
		}

		public async Task<CharacterQuest[]> GetCharacterQuests(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterQuests.Where(q => q.CharacterId == characterId).ToArrayAsync();
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
				.Characters.Include(c => c.Attributes)
				.Include(c => c.Skills)
				.Include(c => c.ScaffoldPairs)
				.Include(c => c.Body)
				.Include(c => c.BodyResources)
				.FirstOrDefaultAsync(c => c.Id == characterId);
		}

		public async Task<CharacterShortcut[]> GetCharacterShortcuts(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterShortcuts.Where(s => s.CharacterId == characterId).OrderBy(s => s.Slot).ToArrayAsync();
		}

		// The spells a character has learned; GrantCharacterSpells replays these as DispatchLearnSpell grants on login.
		public async Task<CharacterSpell[]> GetCharacterSpells(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterSpells.Where(s => s.CharacterId == characterId).OrderBy(s => s.SpellId).ToArrayAsync();
		}

		// The character's stat sheet (CharacterStat) in wire order (Ordinal) - the FullChar snapshot's stat block,
		// authored per-character into the tails. Empty for a freshly-created character (falls back to the skeleton).
		public async Task<CharacterStat[]> GetCharacterStats(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterStats.Where(s => s.CharacterId == characterId).OrderBy(s => s.Ordinal).ToArrayAsync();
		}

		public async Task<uint[]> GetCharacterTags(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterTags.Where(t => t.CharacterId == characterId).Select(t => t.TagId).ToArrayAsync();
		}

		// The abilities a character of `class` has access to up to `level`, from the class<->ability ACCESS map
		// (ClassAbility): true multi-class membership with a PER-CLASS unlock level, derived from the per-character
		// grants (CaptureAbilityImporter.DeriveClassAbilitiesAsync) - the data-driven replacement for the old
		// IsStartingAbility flag. Coverage is whatever the captures supplied (the classes we have captures for).
		public async Task<Ability[]> GetClassAbilities(uint level, Class @class)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.ClassAbilities.Where(ca => ca.Class == @class && ca.Level <= level).Include(ca => ca.Ability).Select(ca => ca.Ability).OrderBy(a => a.Slot).ToArrayAsync();
		}

		// The abilities a character of `class` unlocks in the level band (fromExclusive, toInclusive] - the ability
		// analogue of GetClassSpellsUnlockedBetween, used to grant the ding's newly-unlocked abilities on level-up.
		public async Task<Ability[]> GetClassAbilitiesUnlockedBetween(Class @class, uint fromLevelExclusive, uint toLevelInclusive)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.ClassAbilities.Where(ca => ca.Class == @class && ca.Level > fromLevelExclusive && ca.Level <= toLevelInclusive)
				.Include(ca => ca.Ability)
				.OrderBy(ca => ca.Level)
				.ThenBy(ca => ca.AbilityId)
				.Select(ca => ca.Ability)
				.ToArrayAsync();
		}

		// The combos a character of `class` has access to up to `level`, from the class<->combo ACCESS map (ClassCombo)
		// - the combo analogue of GetClassAbilities. Casters have no rows, so they get no combos (see ClassCombo).
		// RANK-SUPERSEDED like the spell bar (GetStartingBarSpells): one combo per family (the highest rank the
		// character qualifies for), so a lvl-24 HoX gets Hell Strikes II, NOT both I and II - matching the retail
		// 0x2076 combo book (Hakotep). Family grouping is ComboFamilyKey.
		public async Task<Combo[]> GetClassCombos(uint level, Class @class)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var candidates = await ctx.ClassCombos.Where(cc => cc.Class == @class && cc.Level <= level).Include(cc => cc.Combo).Select(cc => cc.Combo).ToListAsync();

			return HighestRankPerFamily(candidates);
		}

		// The combos a character of `class` unlocks in the level band (fromExclusive, toInclusive] - the combo analogue
		// of GetClassAbilitiesUnlockedBetween, used to grant the ding's newly-unlocked combos on level-up. Also
		// rank-superseded within the band (a multi-level ding that crosses two ranks of a line grants only the higher).
		public async Task<Combo[]> GetClassCombosUnlockedBetween(Class @class, uint fromLevelExclusive, uint toLevelInclusive)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var candidates = await ctx
				.ClassCombos.Where(cc => cc.Class == @class && cc.Level > fromLevelExclusive && cc.Level <= toLevelInclusive)
				.Include(cc => cc.Combo)
				.Select(cc => cc.Combo)
				.ToListAsync();

			return HighestRankPerFamily(candidates);
		}

		// The class spells whose unlock level (Spells.Level) falls in (fromLevelExclusive, toLevelInclusive] - the
		// spells NEWLY learned crossing from one level to another (the level-up delta). Grants these on a ding.
		public async Task<uint[]> GetClassSpellsUnlockedBetween(Class @class, uint fromLevelExclusive, uint toLevelInclusive)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.ClassSpells.Where(cs => cs.Class == 0 || cs.Class == @class)
				.Join(ctx.Spells, cs => cs.SpellId, s => s.Id, (cs, s) => s)
				.Where(s => s.Level > fromLevelExclusive && s.Level <= toLevelInclusive)
				.OrderBy(s => s.Level)
				.ThenBy(s => s.Id)
				.Select(s => s.Id)
				.Distinct()
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
			return await ctx.ConversationNodes.Include(n => n.Packets).FirstOrDefaultAsync(n => n.NpcName == npcName && n.TypeId == typeId && n.AnswerPath == answerPath);
		}

		// All quest/tag-conditioned variants of a screen (same NpcName/TypeId/AnswerPath, differing ConditionScript).
		// The runtime evaluates each variant's condition against the live player and serves the matching one (the
		// unconditional "" variant is the fallback). Ordered conditioned-first so the caller can take the first pass.
		public async Task<List<ConversationNode>> GetConversationScreens(string npcName, uint typeId, string answerPath)
		{
			if (string.IsNullOrEmpty(npcName))
				return [];

			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var nodes = await ctx.ConversationNodes.Include(n => n.Packets).Where(n => n.NpcName == npcName && n.TypeId == typeId && n.AnswerPath == answerPath).ToListAsync();

			// Conditioned variants first, the unconditional fallback last.
			return nodes.OrderBy(n => string.IsNullOrEmpty(n.ConditionScript)).ToList();
		}

		public async Task<RespawnPoint> GetDefaultSpawn(uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var points = ctx.RespawnPoints.Where(r => r.MapId == mapId);
			// AlwaysKnown starter wins; otherwise any point on the map; null if the map defines none.
			return await points.FirstOrDefaultAsync(r => r.AlwaysKnown) ?? await points.FirstOrDefaultAsync();
		}

		public async Task<uint[]> GetDiscoveredRespawnPointIds(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterRespawnPoints.Where(c => c.CharacterId == characterId).Select(c => c.RespawnPointId).ToArrayAsync();
		}

		public async Task<Shop> GetFirstShop()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Shops.Include(s => s.Items).OrderBy(s => s.Id).FirstOrDefaultAsync();
		}

		public async Task<Guild> GetGuild(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await LoadGuild(ctx, id);
		}

		public async Task<Guild> GetGuildByName(string name, uint realmId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var id = await ctx.Guilds.Where(g => g.RealmId == realmId && g.Name == name).Select(g => g.Id).FirstOrDefaultAsync();

			return id == 0
				? null
				: await LoadGuild(ctx, id);
		}

		public async Task<Item> GetItem(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Items.FirstOrDefaultAsync(i => i.Id == id);
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
			var categories = await ctx.TradepostListings.Where(l => l.Template == template && l.ItemId != l.Template).Select(l => l.ItemId).ToListAsync();
			if (categories.Count == 0)
				return null;

			return categories.GroupBy(c => c).OrderByDescending(g => g.Count()).First().Key;
		}

		public async Task<LuaScript> GetLuaScript(uint scriptId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.LuaScripts.FirstOrDefaultAsync(s => s.Id == scriptId);
		}

		// One mail by its wire MailId (the GetMailByID / GetMailItem target), attachments eager-loaded.
		public async Task<Mail> GetMailByWireId(ulong mailId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Mails.Include(m => m.Attachments).FirstOrDefaultAsync(m => m.MailId == mailId);
		}

		// All mail addressed to a character (newest first), attachments eager-loaded so the codec can build the
		// item preview/grant without a second round-trip. Recipient NAME is the stable key (the wire id is volatile).
		public async Task<List<Mail>> GetMailsForRecipient(string recipientName, uint realmId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Mails.Include(m => m.Attachments).Where(m => m.RealmId == realmId && m.RecipientName == recipientName).OrderByDescending(m => m.Id).ToListAsync();
		}

		public async Task<Map> GetMap(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Maps.FirstOrDefaultAsync(m => m.Id == id);
		}

		// A random sample of the map's loot pool (#273): buildable items (same render constraints as
		// GetRandomBuildableItems - Hash != 0, named, Normal) that this map's captured bags carried. Returns empty
		// when the map has no pool, so the caller falls back to the all-items random sample. SQLite RANDOM() order.
		public async Task<Item[]> GetMapLootSample(uint mapId, int count)
		{
			if (count <= 0)
				return [];

			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.MapLootEntries.Where(e => e.MapId == mapId)
				.Join(ctx.Items, e => e.ItemTemplateId, i => i.Id, (e, i) => i)
				.Where(i => i.Hash != 0 && i.Name != null && i.Name != "" && i.Type == ItemType.Normal && !i.ExcludeFromRandomLoot)
				.OrderBy(_ => EF.Functions.Random())
				.Take(count)
				.ToArrayAsync();
		}

		public async Task<Map[]> GetMaps()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Maps.OrderBy(m => m.Id).ToArrayAsync();
		}

		// Resolves appearance resource refs ("*texscript_1108153") to their client-registry entries (imported
		// from RDB 1000076 by Faolan.Importer --rdb). Empty when the registry hasn't been imported.
		public async Task<Dictionary<string, MorphTarget>> GetMorphTargets(IReadOnlyCollection<string> names)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.MorphTargets.Where(t => names.Contains(t.Name)).ToDictionaryAsync(t => t.Name);
		}

		// The creature TYPE by name (for spawn-from-TYPE - a summoned pet/mount #307/#244) - distinct from
		// GetNpcSpawnByName, which returns a world PLACEMENT. Includes the type's caster Spells + everything
		// TryBuildFromType needs to assemble the body from columns: the stat core, worn equipment, appearance morphs.
		// Null if no such type or it has no lifted stat core (can't be spawned standalone).
		public async Task<Npc> GetNpcByName(string name)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var lowered = name.ToLowerInvariant();
			return await ctx
				.Npcs.Include(t => t.Spells)
				.Include(t => t.StatCore)
				.Include(t => t.Equipment)
				.ThenInclude(e => e.Item) // the worn item's identity (quad + hash) lives in Items via the FK
				.Include(t => t.Morphs)
				.Where(t => t.Name != null && t.Name.ToLower() == lowered && t.StatCore != null)
				.FirstOrDefaultAsync();
		}

		// A captured NPC spawn by name (case-insensitive), any map (used by .spawnnpc and to fetch a mount-entity
		// template like "Fading Black Horse" to spawn at the player). Requires the type to have a lifted stat core.
		public async Task<NpcSpawn> GetNpcSpawnByName(string name)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var lowered = name.ToLowerInvariant();
			return await ctx
				.NpcSpawns.Include(n => n.Npc)
				.ThenInclude(t => t.Spells)
				.Include(n => n.Npc)
				.ThenInclude(t => t.StatCore) // the type stat core, so the spawn builds from columns (TryBuildLive)
				.Include(n => n.Morphs)
				.Include(n => n.Equipment)
				.ThenInclude(e => e.Item) // the worn item's identity (quad + hash) lives in Items via the FK
				.Where(n => n.Npc != null && n.Npc.Name != null && n.Npc.Name.ToLower() == lowered && n.Npc.StatCore != null) // #244: name lives on the type
				.FirstOrDefaultAsync();
		}

		// The NPC entity spawns for a map (the lifted identity/transform/stat columns). The world-entry path builds
		// each NPC's FA015807 body from its row via NpcSpawnBuilder.TryBuildLive - wholly from columns (#243/#244).
		public async Task<NpcSpawn[]> GetNpcSpawns(uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.NpcSpawns.Include(n => n.Morphs)
				.Include(n => n.Equipment)
				.ThenInclude(e => e.Item) // the worn item's identity (quad + hash) lives in Items via the FK
				.Include(n => n.Npc)
				.ThenInclude(t => t.Spells) // #244: the NPC type + its FK'd spells (replaces the name-string spell join)
				.Include(n => n.Npc)
				.ThenInclude(t => t.Chatter) // ambient bark lines for this NPC type
				.Include(n => n.Npc)
				.ThenInclude(t => t.StatCore) // #244: the type stat core, so the spawn body builds from columns
				.Where(n => n.MapId == mapId)
				.ToArrayAsync();
		}

		// The spells a caster NPC casts (keyed by its stable name), for the sim's NPC spell-casting (#232).
		public async Task<List<NpcSpell>> GetNpcSpellsByName(string name)
		{
			if (string.IsNullOrEmpty(name))
				return [];

			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var lowered = name.ToLowerInvariant();
			return await ctx.NpcSpells.Where(s => s.Npc != null && s.Npc.Name != null && s.Npc.Name.ToLower() == lowered).ToListAsync();
		}

		// A map's captured patrol routes (#266 patrol), each with its ordered waypoints. Keyed at the paired NPC's
		// spawn position (the importer correlates route->spawn by handle and stores the route at the spawn point), so
		// the world model links a route to its NPC by exact position.
		public async Task<PatrolPath[]> GetPatrolPaths(uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.PatrolPaths.Include(p => p.Waypoints).Where(p => p.MapId == mapId).ToArrayAsync();
		}

		// The shared journal-content definition for a quest (text refs + objective marker), or null if not seeded.
		public async Task<QuestDefinition> GetQuestDefinition(uint questId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.QuestDefinitions.Include(q => q.TextRefs).Include(q => q.Goals).FirstOrDefaultAsync(q => q.QuestId == questId);
		}

		// NPCs whose world spawn is GATED on a quest (#261): an NPC that is a KILL-goal target of some quest needs to
		// be present only while a player carries that quest, so it is streamed solely to holders (interest gating).
		// Quest GIVERS are excluded - they must always be visible to hand the quest out (Casilda is both a giver and a
		// later talk/escort target, so she stays ungated). Returns NpcName -> the quest ids that reveal it. Data-driven,
		// so any future quest with a kill target gets the same treatment automatically.
		public async Task<Dictionary<string, uint[]>> GetQuestGatedNpcMap()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var givers = (await ctx.ConversationNodes.Where(n => n.GrantQuestId != 0 && n.NpcName != null).Select(n => n.NpcName).Distinct().ToListAsync()).ToHashSet();

			var killTargets = await ctx
				.QuestGoalDefinitions.Where(g => g.GoalType == QuestGoalType.Kill && g.TargetName != null && g.TargetName != "")
				.Join(ctx.QuestDefinitions, g => g.QuestDefinitionId, d => d.Id, (g, d) => new
				{
					g.TargetName,
					d.QuestId
				})
				.Distinct()
				.ToListAsync();

			return killTargets.Where(x => !givers.Contains(x.TargetName)).GroupBy(x => x.TargetName).ToDictionary(g => g.Key, g => g.Select(x => x.QuestId).Distinct().ToArray());
		}

		// Quest-giver NPC name -> the quest id it grants (from ConversationNodes.GrantQuestId). Drives the per-NPC
		// quest marker (!/?) - which NPC offers/turns-in which quest (#241). One quest per name (the lowest id if an
		// NPC grants several); cached by the caller.
		public async Task<Dictionary<string, uint>> GetQuestGiverMap()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var grants = await ctx
				.ConversationNodes.Where(n => n.GrantQuestId != 0 && n.NpcName != null)
				.Select(n => new
				{
					n.NpcName,
					n.GrantQuestId
				})
				.Distinct()
				.ToListAsync();
			return grants.GroupBy(g => g.NpcName).ToDictionary(g => g.Key, g => g.Min(x => x.GrantQuestId));
		}

		// A random sample of snapshot-buildable gear/normal items (Hash != 0, so SnapshotItemBuilder can
		// render them; not a Quest or Resource item, so those don't end up as random loot). Used to fill loot bags
		// with a couple of test items (.spawnbag, NPC-kill drops). SQLite RANDOM() order; fewer than `count` come
		// back only when the item table has fewer matching rows.
		public async Task<Item[]> GetRandomBuildableItems(int count)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.Items.Where(i => i.Hash != 0 && i.Name != null && i.Name != "" && i.Type == ItemType.Normal && !i.ExcludeFromRandomLoot)
				.OrderBy(_ => EF.Functions.Random())
				.Take(count)
				.ToArrayAsync();
		}

		// Normal-typed items explicitly flagged out of random loot (#273) - i.e. the unique/quest items that needed the
		// flag because their Type didn't already exclude them. (Quest/Resource items are excluded by Type, so listing
		// them too would just flood the .noloot list output.)
		public async Task<Item[]> GetRandomLootExcludedItems()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Items.Where(i => i.ExcludeFromRandomLoot && i.Type == ItemType.Normal).OrderBy(i => i.Name).ToArrayAsync();
		}

		public async Task<Realm> GetRealm(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Realms.FirstOrDefaultAsync(r => r.Id == id);
		}

		public async Task<Realm[]> GetRealms()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Realms.OrderBy(r => r.Id).ToArrayAsync();
		}

		// #324: a realm game host flips its own row's online status as it starts (Online) / stops (Offline), so the
		// login tier's realm list (SendRealmList) reflects which realms are actually up. Bulk update - no entity load.
		public async Task SetRealmOnlineStatus(uint realmId, RealmOnlineStatus status)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Realms.Where(r => r.Id == realmId).ExecuteUpdateAsync(s => s.SetProperty(r => r.OnlineStatus, status));
		}

		// Recent chat history newest-first, optionally for a single channel (#95). For the web/MCP log + moderation.
		public async Task<ChatMessage[]> GetRecentChatMessages(int limit = 100, uint? channelId = null)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var q = ctx.ChatMessages.AsNoTracking();
			if (channelId.HasValue)
				q = q.Where(m => m.ChannelId == channelId.Value);
			// Order by the auto-increment Id (monotonic with insertion = chronological); SQLite can't ORDER BY a
			// DateTimeOffset column.
			return await q.OrderByDescending(m => m.Id).Take(limit).ToArrayAsync();
		}

		// The crafting recipe-book catalog, ordered by recipe id (== the captured TradeskillReplyIIR record order),
		// so the reconstructed list reproduces the capture byte-for-byte.
		public async Task<Recipe[]> GetRecipes()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Recipes.OrderBy(r => r.Id).ToArrayAsync();
		}

		// A single resurrection point by id (resolves a character's bind point to its map+position on recall).
		public async Task<RespawnPoint> GetRespawnPoint(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.RespawnPoints.FirstOrDefaultAsync(p => p.Id == id);
		}

		public async Task<RespawnPoint[]> GetRespawnPoints(uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.RespawnPoints.Where(r => r.MapId == mapId).ToArrayAsync();
		}

		public async Task<Shop> GetShopByVendorName(string vendorName)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Shops.Include(s => s.Items).FirstOrDefaultAsync(s => s.VendorName == vendorName);
		}

		public async Task<Spell> GetSpell(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Spells.FirstOrDefaultAsync(s => s.Id == id);
		}

		public async Task<Spell[]> GetSpells()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Spells.OrderBy(s => s.Id).ToArrayAsync();
		}

		// The spells to place on the DEFAULT action bar: ONE per spell family (the highest rank the character can use),
		// so the bar shows one icon per spell instead of every rank (Ice Strike R1/R2/R3... all share one icon, which
		// makes the bar look like it "repeats"). The full ranked set is still LEARNED via GetStartingSpells; this is
		// bar placement only. Families are ordered by when first acquired (their lowest unlock level) so the bar reads
		// low -> high. Family key = the spell name with a trailing " (Rank N)" stripped.
		public async Task<uint[]> GetStartingBarSpells(uint level, Class @class)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var candidates = await ctx
				.ClassSpells.Where(cs => cs.Class == 0 || cs.Class == @class)
				.Join(ctx.Spells, cs => cs.SpellId, s => s.Id, (cs, s) => s)
				// Level 0 = UNKNOWN unlock level (not yet imported - most of the catalog). Exclude it: treating it as
				// "<= level" put every unleveled spell (incl. high ranks like "Aura of Dread Fury (Rank 5)") on a
				// level-1 character's bar. Only spells with a KNOWN unlock level at/under the character seed the bar.
				.Where(s => s.Level > 0 && s.Level <= level)
				.Select(s => new
				{
					s.Id,
					s.Name,
					s.Level
				})
				.ToListAsync();

			return candidates
				.GroupBy(s => StripRankSuffix(s.Name))
				.Select(g => new
				{
					BestId = g.OrderByDescending(s => s.Level).ThenByDescending(s => s.Id).First().Id,
					FirstLevel = g.Min(s => s.Level)
				})
				.OrderBy(x => x.FirstLevel)
				.ThenBy(x => x.BestId)
				.Select(x => x.BestId)
				.ToArray();
		}

		// SPELLS a character has learned by its current level (#74): every spell its class (or the generic Class 0 set)
		// has ACCESS to (ClassSpells) whose unlock level (Spells.Level - NOT ClassSpells, which is pure membership) is
		// at or below `level`. ClassSpells now holds the full per-class progression (ellatha scrape); the create
		// captures are the level-1 truth, so the importer pins the create-granted spells to Level 1 and pushes the
		// rest above it (a level-1 create must yield exactly the capture's f239 grant). The spell analogue of
		// GetClassAbilities.
		public async Task<uint[]> GetStartingSpells(uint level, Class @class)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.ClassSpells.Where(cs => cs.Class == 0 || cs.Class == @class)
				.Join(ctx.Spells, cs => cs.SpellId, s => s.Id, (cs, s) => s)
				// Level 0 = UNKNOWN unlock level (not yet imported). Exclude it so a fresh level-1 character's
				// spellbook is not seeded with every unleveled spell in the class map (which are mostly high-rank,
				// 0-mana placeholders). Only KNOWN-leveled spells at/under the character's level are learned.
				.Where(s => s.Level > 0 && s.Level <= level)
				.OrderBy(s => s.Level)
				.ThenBy(s => s.Id)
				.Select(s => s.Id)
				.Distinct()
				.ToArrayAsync();
		}

		public async Task<Dictionary<string, byte[]>> GetStockPhrases()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.StockPhrases.ToDictionaryAsync(p => p.Key, p => p.RemoteFormat);
		}

		// One listing by its unique sale id - the buy targets this (TradepostBuyItem [saleId]).
		public async Task<TradepostListing> GetTradepostListingBySaleId(ulong saleId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.TradepostListings.FirstOrDefaultAsync(l => l.SaleId == saleId);
		}

		// The seller's active listing for a given origin bank slot - how the seller's own change-price / cancel ops
		// address their listing (they carry nBankItemSlot, not the saleId). Keyed by (seller, slot).
		public async Task<TradepostListing> GetTradepostListingBySellerSlot(string sellerName, uint bankSlot, uint realmId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.TradepostListings.FirstOrDefaultAsync(l => l.RealmId == realmId && l.SellerName == sellerName && l.BankSlot == bankSlot);
		}

		// Auction-house listings for a searched item template, cheapest first (the order the grid renders). Price is
		// a ulong (tin), which SQLite can't ORDER BY in SQL, so fetch the (small) match set and sort client-side.
		// Realm-scoped (#324): the AH is realm-local, so only this realm's listings.
		public async Task<List<TradepostListing>> GetTradepostListings(uint itemId, uint realmId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var rows = await ctx.TradepostListings.Where(l => l.RealmId == realmId && l.ItemId == itemId).ToListAsync();
			return rows.OrderBy(l => l.Price).ToList();
		}

		public async Task<WorldObject[]> GetWorldObjects(uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.WorldObjects.Where(o => o.MapId == mapId && o.DisplayName != null).ToArrayAsync();
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

		public async Task RemoveCharacterShortcut(uint characterId, uint slot)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.CharacterShortcuts.Where(s => s.CharacterId == characterId && s.Slot == slot).ExecuteDeleteAsync();
		}

		// Removes one escrowed attachment from a mail once the recipient has taken it into the bag (GetMailItem).
		public async Task RemoveMailAttachment(ulong mailId, ulong itemInstanceId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var mail = await ctx.Mails.FirstOrDefaultAsync(m => m.MailId == mailId);
			if (mail == null)
				return;

			await ctx.MailAttachments.Where(a => a.MailId == mail.Id && a.ItemInstanceId == itemInstanceId).ExecuteDeleteAsync();
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
			tracked.GmGodMode = account.GmGodMode;
			tracked.GmInvisible = account.GmInvisible;
			await ctx.SaveChangesAsync();
		}

		// Persists a (detached) character graph - the character row plus its Body if present - as a single unit of
		// work. Used for the create-editor's accumulated edits (sex/race/morphs/name) and in-world character edits.
		public async Task SaveCharacter(Character character)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			ctx.Characters.Update(character); // TrackGraph: default (store-generated) keys -> Added, set keys -> Modified

			// Dependents keyed on a non-store-generated value - the shared-PK one-to-ones (Body/MaxStats on
			// CharacterId) and the compound-PK junctions - carry a SET key even when brand-new, so Update() marks a new
			// one Modified and SaveChanges would fail with a 0-rows concurrency error. Flip any Modified dependent whose
			// row doesn't actually exist yet to Added so it inserts instead. (SaveCharacter is the cold full-graph path;
			// hot updates use the targeted ExecuteUpdate savers below.)
			foreach (var entry in ctx.ChangeTracker.Entries().Where(e => e is { State: EntityState.Modified, Entity: not Character }).ToList())
			{
				if (await entry.GetDatabaseValuesAsync() is null)
					entry.State = EntityState.Added;
			}

			await ctx.SaveChangesAsync();
		}

		// Persists a character's bind / recall point (the discovered RespawnPoint the Totem of Origin returns to) the
		// moment it's set, so it survives relog independently of the full-graph save. See issue #64.
		public async Task SaveCharacterBind(uint characterId, uint respawnPointId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Characters.Where(c => c.Id == characterId).ExecuteUpdateAsync(s => s.SetProperty(c => c.BindRespawnPointId, respawnPointId));
		}

		// Persists a character's DEAD state (#214) the moment it changes - on death (true) and respawn (false) - so a
		// player who logs out dead returns dead and the login snapshot carries current-health = 0. Independent of the
		// full-graph save, like SaveCharacterBind/SetCharacterProgress.
		public async Task SaveCharacterDeadState(uint characterId, bool isDead)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Characters.Where(c => c.Id == characterId).ExecuteUpdateAsync(s => s.SetProperty(c => c.IsDead, isDead));
		}

		public async Task SaveCharacterEquipment(CharacterEquipment equipment)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();

			// Load (or create) the tracked row and copy each slot's item by id onto it. Setting a navigation on a
			// TRACKED entity runs EF's relationship fixup so the FK column updates correctly - INCLUDING being
			// cleared to NULL when a slot is emptied (Attach/Update of a detached graph does NOT reliably null a
			// reference-nav FK, which is what silently lost the unequip/clear-bag writes).
			var tracked = await LoadTrackedEquipment(ctx, equipment.CharacterId) ?? AddNewEquipment(ctx, equipment.CharacterId);

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

			ctx.CharacterAttributes.AddRange(character.Attributes);
			ctx.CharacterSkills.AddRange(character.Skills);
			ctx.CharacterScaffoldPairs.AddRange(character.ScaffoldPairs);

			await ctx.SaveChangesAsync();
		}

		// Persists the character's authoritative live transform (the WorldEntity's position/rotation/map) on
		// teleport / disconnect / autosave - movement isn't otherwise saved, so without this a relog loses where the
		// player was.
		public async Task SaveCharacterTransform(uint characterId, Vector3 position, Quaternion rotation, uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx
				.Characters.Where(c => c.Id == characterId)
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

		// Persists one chat line (#95): a channel post or a whisper, recorded write-behind by the ChatServer so chat
		// history survives a restart. Never throws back into the chat relay - a persistence hiccup must not drop the
		// live message - so the caller wraps it defensively.
		public async Task SaveChatMessage(ChatMessage message)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			ctx.ChatMessages.Add(message);
			await ctx.SaveChangesAsync();
		}

		public async Task SaveGuild(Guild guild)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			// Detach the members navigation: only the guild row itself (MOTD/description/cash/...) is saved here;
			// membership changes go through SetCharacterGuild.
			ctx.Entry(guild).State = EntityState.Modified;
			await ctx.SaveChangesAsync();
		}

		// Items whose name contains the substring, case-insensitive (LOWER() both sides). Discovery for the
		// MCP search_items tool and .additem.
		public async Task<Item[]> SearchItems(string nameSubstring, int limit = 25)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var lowered = nameSubstring.ToLowerInvariant();
			return await ctx.Items.Where(i => i.Name != null && i.Name.ToLower().Contains(lowered)).OrderBy(i => i.Name).Take(limit).ToArrayAsync();
		}

		// Captured NPC spawn rows whose name contains the substring, case-insensitive, any map. Rows whose type
		// has no lifted stat core are included (still useful to know the NPC exists) - callers check StatCore for
		// spawnability.
		public async Task<NpcSpawn[]> SearchNpcSpawns(string nameSubstring, int limit = 50)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var lowered = nameSubstring.ToLowerInvariant();
			return await ctx
				.NpcSpawns.Include(n => n.Npc)
				.ThenInclude(t => t.StatCore) // so the Spawnable flag can check buildability (StatCore.IirHeader)
				.Where(n => n.Npc != null && n.Npc.Name != null && n.Npc.Name.ToLower().Contains(lowered)) // #244: name lives on the type
				.OrderBy(n => n.Npc.Name)
				.ThenBy(n => n.MapId)
				.Take(limit)
				.ToArrayAsync();
		}

		// Set an account's stored password (already hashed by the caller). Returns false if no such account. Used to
		// migrate a legacy plaintext password to a hash on first successful login.
		public async Task<bool> SetAccountPassword(uint accountId, string password)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var tracked = await ctx.Accounts.FirstOrDefaultAsync(a => a.Id == accountId);
			if (tracked == null)
				return false;

			tracked.Password = password;
			await ctx.SaveChangesAsync();
			return true;
		}

		// Set an account's State (e.g. Banned/Active) directly. Returns false if no such account. Used by the .ban GM
		// command and the web admin panel; login rejects any account whose State != Active.
		public async Task<bool> SetAccountState(uint accountId, AccountState state)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var tracked = await ctx.Accounts.FirstOrDefaultAsync(a => a.Id == accountId);
			if (tracked == null)
				return false;

			tracked.State = state;
			await ctx.SaveChangesAsync();
			return true;
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
			await ctx.Characters.Where(c => c.Id == characterId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Money, money));
		}

		// Persists a character's levelling progress (within-level Experience + Level) the moment it changes, so XP and
		// levels survive relog without depending on the full-graph SaveCharacter at disconnect/autosave.
		public async Task SetCharacterProgress(uint characterId, uint experience, ushort level)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Characters.Where(c => c.Id == characterId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Experience, experience).SetProperty(c => c.Level, level));
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

		public Task<int> SetCreationSpawn(Character character)
		{
			character.MapId = Map.TortageBeachId;
			ApplyCreationSpawn(character);
			return SaveCharacterReturningRows(character);
		}

		// Sets (or clears) an item's random-loot exclusion (#273), persisted to faolan.db. Returns false when no such
		// item exists. Used by the .noloot GM command so a unique/quest item spotted in-client can be excluded live.
		public async Task<bool> SetItemRandomLootExcluded(uint itemId, bool excluded)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var item = await ctx.Items.FirstOrDefaultAsync(i => i.Id == itemId);
			if (item == null)
				return false;

			item.ExcludeFromRandomLoot = excluded;
			await ctx.SaveChangesAsync();
			return true;
		}

		// Updates a mail's attached money (tin); 0 clears it once the recipient has banked it (#213).
		public async Task SetMailMoney(ulong mailId, ulong money)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Mails.Where(m => m.MailId == mailId).ExecuteUpdateAsync(s => s.SetProperty(m => m.Money, money));
		}

		// Marks a mail read/unread (the footer word flips 0x0b unread -> 0x09 read on the wire).
		public async Task SetMailRead(ulong mailId, bool read)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Mails.Where(m => m.MailId == mailId).ExecuteUpdateAsync(s => s.SetProperty(m => m.IsRead, read));
		}

		// Reprices a listing (TradepostChangeItemPrice).
		public async Task SetTradepostListingPrice(ulong saleId, ulong price)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.TradepostListings.Where(l => l.SaleId == saleId).ExecuteUpdateAsync(s => s.SetProperty(l => l.Price, price));
		}

		public async Task<bool> UpdateLastInfo(Account account, string ipAddress)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var tracked = await ctx.Accounts.FirstOrDefaultAsync(a => a.Id == account.Id);
			if (tracked == null)
				return false;

			tracked.LastConnection = DateTime.UtcNow;
			tracked.LastIpAddress = ipAddress;
			account.LastConnection = tracked.LastConnection; // keep the in-memory copy in sync
			account.LastIpAddress = tracked.LastIpAddress;
			return await ctx.SaveChangesAsync() > 0;
		}

		public async Task<bool> UpdateLastInfo(Character character, string ipAddress)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var tracked = await ctx.Characters.FirstOrDefaultAsync(c => c.Id == character.Id);
			if (tracked == null)
				return false;

			tracked.LastConnection = DateTime.UtcNow;
			tracked.LastIpAddress = ipAddress;
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

		private static CharacterEquipment AddNewEquipment(DatabaseContext ctx, uint characterId)
		{
			var equipment = new CharacterEquipment
			{
				CharacterId = characterId
			};
			ctx.CharacterEquipment.Add(equipment);
			return equipment;
		}

		private static void ApplyCreationSpawn(Character character)
		{
			character.Position = Map.NewPlayerSpawnSpawnPoint.Position;
		}

		// The combo-family key: groups the ranks of one combo LINE (Hell Strikes I/II/III...) so the grant can pick
		// just the highest rank the character qualifies for - the combo analogue of StripRankSuffix for spells. Combo
		// catalog names are ENGINE names ({Class}_{level}_{tags}_{Display Name}_{RankRoman}, e.g.
		// Herald_05_multihit_Hell_Strikes_I), so the family is the trailing DISPLAY-name run (Capitalized words +
		// interior connectors like "to"/"and"), normalised (lowercased, separators stripped) after dropping the class
		// prefix and a trailing roman-numeral rank. This groups I..V of a line regardless of the differing level/tag
		// prefix, and merges the "80b" / space-in-name / "khitai" variants that share a display name (validated in
		// Python against the Herald catalog: a lvl-24 HoX collapses to the 5 the retail 0x2076 book shows).
		private static string ComboFamilyKey(string name)
		{
			if (string.IsNullOrEmpty(name))
				return name ?? "";

			var tokens = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
			if (tokens.Length <= 1)
				return name.ToLowerInvariant();

			var end = tokens.Length;
			if (IsRomanNumeral(tokens[end - 1])) // drop a trailing rank
				end--;

			// Walk back over the trailing display run (stop before the class prefix at index 0).
			var start = end;
			for (var i = end - 1; i >= 1; i--)
			{
				var t = tokens[i];
				if (t.Length > 0 && (char.IsUpper(t[0]) || ComboNameConnectors.Contains(t)))
					start = i;
				else
					break;
			}

			if (start >= end) // no display run found - fall back to the whole name
				return name.ToLowerInvariant().Replace("_", "").Replace(" ", "");

			return string.Concat(tokens[start..end]).ToLowerInvariant().Replace(" ", "");
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
			return ctx.Database.ExecuteSqlRawAsync("INSERT OR IGNORE INTO Maps (Id, Name, InstanceMode, PopulationCap) VALUES ({0}, {1}, 0, 0)", mapId, name);
		}

		// Collapses a combo set to one entry per family - the highest rank (by Level, then Id) - ordered low->high by
		// first-acquired level. The combo twin of the spell-bar family collapse in GetStartingBarSpells.
		private static Combo[] HighestRankPerFamily(IEnumerable<Combo> combos)
		{
			return combos.GroupBy(c => ComboFamilyKey(c.Name)).Select(g => g.OrderByDescending(c => c.Level).ThenByDescending(c => c.Id).First()).OrderBy(c => c.Level).ThenBy(c => c.Id).ToArray();
		}

		// True for a combo rank token (I, II, III, IV, V, VI, VII, ...): uppercase roman numeral characters only.
		private static bool IsRomanNumeral(string s)
		{
			if (string.IsNullOrEmpty(s) || s.Length > 5)
				return false;

			foreach (var c in s)
			{
				if (c is not ('I' or 'V' or 'X'))
					return false;
			}

			return true;
		}

		// Guild with the full member roster (the 0x203F full update needs every member's name/level/class).
		private static Task<Guild> LoadGuild(DatabaseContext ctx, uint id)
		{
			return ctx.Guilds.Include(g => g.Members).FirstOrDefaultAsync(g => g.Id == id);
		}

		private static Task<CharacterEquipment> LoadTrackedEquipment(DatabaseContext ctx, uint characterId)
		{
			return ctx
				.CharacterEquipment.Include(e => e.Neck)
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

		private static async Task<Item> ResolveItem(DatabaseContext ctx, uint? id)
		{
			return id == null
				? null
				: await ctx.Items.FindAsync(id.Value);
		}

		// The spell-family key: the name with a trailing " (Rank N)" removed (and trailing spaces trimmed - a few
		// catalog names carry a double space before the rank, e.g. "Freeze  (Rank 2)").
		private static string StripRankSuffix(string name)
		{
			if (string.IsNullOrEmpty(name))
				return name ?? "";

			var i = name.IndexOf(" (Rank ", StringComparison.Ordinal);
			return (i >= 0
				? name[..i]
				: name).TrimEnd();
		}
	}
}
