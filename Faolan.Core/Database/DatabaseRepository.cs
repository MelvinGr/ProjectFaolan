using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
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
		Task<Character[]> GetCharactersByAccount(uint id, bool skipUninitialized = true);
		Task<Character> GetCharacterScaffold(uint characterId);
		Task<CharacterShortcut[]> GetCharacterShortcuts(uint characterId);
		Task<uint[]> GetCharacterTags(uint characterId);
		Task RemoveCharacterShortcut(uint characterId, uint slot);
		Task SetCharacterShortcut(uint characterId, uint slot, uint shortcutType, uint targetId);

		Task<ClassAbility> GetClassAbility(uint @class, uint level);

		Task<ConversationNode> GetConversationNode(uint npcHandle, uint typeId, uint mode, uint field3, bool exactHandleOnly = false);
		Task<ConversationNode> GetConversationNodeByName(string npcName, uint typeId, uint mode, uint field3);
		Task<uint[]> GetDiscoveredRespawnPointIds(uint characterId);

		Task<Guild> CreateGuild(string name, uint leaderCharacterId);
		Task<Guild> GetGuild(uint id);
		Task<Guild> GetGuildByName(string name);
		Task SaveGuild(Guild guild);
		Task<bool> SetCharacterGuild(uint characterId, uint? guildId, byte rank);

		Task<Item> GetItem(uint id);
		Task<LuaScript> GetLuaScript(uint scriptId);

		Task<Map> GetMap(uint id);
		Task<Map[]> GetMaps();

		Task<Shop> GetShopByVendorName(string vendorName);
		Task<Shop> GetFirstShop();

		Task<MapWorldBurst> GetMapWorldBurst(uint mapId);
		Task<Dictionary<string, MorphTarget>> GetMorphTargets(IReadOnlyCollection<string> names);
		Task<NpcSpawn> GetNpcSpawnByName(string name);
		Task<NpcSpawn[]> GetNpcSpawns(uint mapId);

		Task<Realm> GetRealm(uint id);
		Task<Realm[]> GetRealms();
		Task<RespawnPoint[]> GetRespawnPoints(uint mapId);

		Task<Spell> GetSpell(uint id);
		Task<Spell[]> GetSpells();
		Task<Ability[]> GetStartingAbilities(uint level);
		Task<WorldObject[]> GetWorldObjects(uint mapId);
		Task<T> Query<T>(Func<IDatabaseContext, Task<T>> work);
		Task RemoveCharacterQuest(uint characterId, uint questId);
		Task SaveAccount(Account account);
		Task SaveCharacter(Character character);
		Task SaveCharacterEquipment(CharacterEquipment equipment);
		Task SaveCharacterScaffold(Character character);
		Task SetCharacterInventorySlot(uint characterId, int container, int slot, uint? itemId, uint count = 1);
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

		// Slave Galley (character-creation) map. 0xFAA/4010 is the map the retail create capture's world
		// entry uses (account-stricken998-character-strickenen-create-random-actions) and it exists in the
		// seeded Maps table; 0xFA0/4000 does NOT exist and caused a Map foreign-key failure on save.
		private const uint CreationMapId = Map.TortageBeachId;

		// Character ids ARE the on-wire client instance and the burst replay substitutes them into captured 4-byte
		// protobuf-varint slots, so they must encode to <= 4 varint bytes (id <= 0x0FFFFFFF). Allocate from this base
		// continuing the existing 0x013xxxxx rows; a 5-byte id (> 0x0FFFFFFF) crashes the client (see DatabaseContext).
		private const uint FirstCharacterId = 0x02000000;
		private const uint MaxCharacterId = 0x0FFFFFFF;

		// Tortage Beach intro spawn, taken verbatim (exact IEEE-754 bits) from the dikketest world-entry
		// MapChange so a newly created character loads at the correct intro location instead of (0,0,0).
		private static readonly float CreationSpawnX = BitConverter.Int32BitsToSingle(0x43A14000); // 322.5
		private static readonly float CreationSpawnY = BitConverter.Int32BitsToSingle(0x43114FB3); // ~145.31
		private static readonly float CreationSpawnZ = BitConverter.Int32BitsToSingle(0x4430399A); // ~704.90

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
			await EnsureMapExists(ctx, CreationMapId, "Tortage Beach");

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
					MapId = CreationMapId
				};

				// Seed default vitals (cascade-inserts with the character). Class isn't chosen yet at row
				// allocation, so this is the level-1 rogue-default baseline; regenerate when class is committed.
				character.MaxStats = CharacterMaxStats.CreateDefaults(character.Class, character.Level);

				ApplyCreationSpawn(character);
				ctx.Characters.Add(character);
				await ctx.SaveChangesAsync();
			}
			else if (character.MapId != CreationMapId || character.PositionX != CreationSpawnX)
			{
				// Older pending rows may predate the creation-map/spawn default (or carry a non-existent
				// map); align them to the Tortage Beach map + intro spawn so world entry loads at the right
				// place and won't fail the Map foreign key.
				character.MapId = CreationMapId;
				ApplyCreationSpawn(character);
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

		// The class/level row that lists which abilities (and spells) a character of this class+level should have.
		// Falls back to the highest captured level at or below the requested one so any level still resolves a grant.
		public async Task<ClassAbility> GetClassAbility(uint @class, uint level)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.ClassAbilities
				.Where(c => c.Class == @class && c.Level <= level)
				.OrderByDescending(c => c.Level)
				.FirstOrDefaultAsync();
		}

		public async Task<ConversationNode> GetConversationNode(uint npcHandle, uint typeId, uint mode, uint field3, bool exactHandleOnly = false)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();

			// Prefer the node captured for THIS exact NPC handle - the correct per-NPC dialogue (handles are stable
			// because scenes are replayed verbatim). (TypeId, Mode, Field3) is NOT NPC-unique, so the args-only
			// match can be the wrong NPC's conversation.
			var exact = await ctx
				.ConversationNodes
				.Include(n => n.Packets)
				.FirstOrDefaultAsync(n => n.CapturedNpcHandle == npcHandle && n.TypeId == typeId && n.Mode == mode && n.Field3 == field3);
			if (exact != null || exactHandleOnly)
				return exact;

			// Args-only fallback (the pre-handle behaviour) so a captured conversation still plays for an NPC whose
			// handle differs from the replayed scene's. Callers that can't tolerate a wrong-NPC match (the create
			// world intro, where the scripted fallback is preferable) pass exactHandleOnly.
			return await ctx
				.ConversationNodes
				.Include(n => n.Packets)
				.Where(n => n.TypeId == typeId && n.Mode == mode && n.Field3 == field3)
				.OrderByDescending(n => n.Packets.Count)
				.FirstOrDefaultAsync();
		}

		// Routes a conversation by the clicked NPC's stable NAME (handles are session-volatile). Exact match on
		// (NpcName, TypeId, Mode, Field3) - the (Mode, Field3) still select the branch within that NPC's dialogue;
		// the name guarantees the RIGHT NPC, which the old handle/args-only lookup could not across captures.
		public async Task<ConversationNode> GetConversationNodeByName(string npcName, uint typeId, uint mode, uint field3)
		{
			if (string.IsNullOrEmpty(npcName))
				return null;

			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.ConversationNodes
				.Include(n => n.Packets)
				.FirstOrDefaultAsync(n => n.NpcName == npcName && n.TypeId == typeId && n.Mode == mode && n.Field3 == field3);
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
		// A captured NPC spawn by name, any map (used to fetch a mount-entity template like "Fading Black Horse" to
		// spawn at the player). Prefers a row that has spawn Data.
		public async Task<NpcSpawn> GetNpcSpawnByName(string name)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.NpcSpawns
				.Where(n => n.Name == name && n.Data != null)
				.FirstOrDefaultAsync();
		}

		// The captured NPC entity spawns for a map (FA015807 bodies in Data + the lifted identity/transform
		// columns). The world-entry path builds each NPC's spawn from its row via NpcSpawnBuilder.
		public async Task<NpcSpawn[]> GetNpcSpawns(uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.NpcSpawns
				.Where(n => n.MapId == mapId && n.Data != null)
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
		public async Task<Ability[]> GetStartingAbilities(uint level)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.Abilities
				.Where(a => a.IsStartingAbility && a.Level <= level)
				.OrderBy(a => a.Slot)
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
			character.MapId = CreationMapId;
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
			character.PositionX = CreationSpawnX;
			character.PositionY = CreationSpawnY;
			character.PositionZ = CreationSpawnZ;
		}

		// Inserts a Map row only if its id is absent (INSERT OR IGNORE keeps an existing seeded row, e.g.
		// 4010 "Tortage Beach", untouched). Used so capture-referenced maps that aren't seeded still
		// satisfy the foreign key.
		private static Task EnsureMapExists(DatabaseContext ctx, uint mapId, string name)
		{
			return ctx.Database.ExecuteSqlRawAsync("INSERT OR IGNORE INTO Maps (Id, Name, PositionX, PositionY, PositionZ, RotationX, RotationY, RotationZ) VALUES ({0}, {1}, 0, 0, 0, 0, 0, 0)", mapId, name);
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
