using System;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database
{
	public interface IDatabaseRepository
	{
		Task<bool> CheckLogin(string username, string password);
		Task<Character> CreateCharacter(uint accountId, uint realmId);

		Task<bool> DeleteCharacter(uint characterId);

		// Ad-hoc unit of work: runs `work` against a fresh short-lived context and disposes it. Use for one-off
		// reads/writes that do not have a dedicated method. Entities returned are detached.
		Task Execute(Func<IDatabaseContext, Task> work);

		Task<Ability> GetAbility(uint id);

		Task<Account> GetAccount(uint id);
		Task<Account> GetAccount(string userName);

		Task<ClassAbility> GetClassAbility(uint @class, uint level);

		Task<Character> GetCharacter(uint id);

		Task<CharacterEquipment> GetCharacterEquipment(uint characterId);
		Task<Character> GetCharacterForAccount(uint accountId, uint clientInstance);
		Task<Character[]> GetCharactersByAccount(uint id, bool skipUninitialized = true);

		Task<ConversationNode> GetConversationNode(uint npcHandle, uint typeId, uint mode, uint field3, bool exactHandleOnly = false);
		Task<ConversationNode> GetConversationNodeByName(string npcName, uint typeId, uint mode, uint field3);

		Task<Item> GetItem(uint id);

		Task<Map> GetMap(uint id);
		Task<Map[]> GetMaps();

		Task<MapWorldBurst> GetMapWorldBurst(uint mapId);

		Task<Realm> GetRealm(uint id);
		Task<Realm[]> GetRealms();

		Task<Spell> GetSpell(uint id);
		Task<Spell[]> GetSpells();
		Task<T> Query<T>(Func<IDatabaseContext, Task<T>> work);
		Task SaveAccount(Account account);
		Task SaveCharacter(Character character);
		Task SaveCharacterEquipment(CharacterEquipment equipment);
		Task<int> SetCreationSpawn(Character character);

		Task<bool> UpdateLastInfo(Account account, INetworkClient client);
		Task<bool> UpdateLastInfo(Character character, INetworkClient client);
	}

	public class DatabaseRepository : IDatabaseRepository
	{
		// Character ids are allocated in the 4-byte protobuf-varint range [0x02000000, 0x0FFFFFFF] so the id
		// (which doubles as the on-wire client instance) always encodes to a 4-byte varint - the same length as
		// the capture placeholder 0x021662C8 - keeping the verbatim packets' nested length prefixes valid when
		// the instance is substituted. (SQLite autoincrement was seeded high by a manual char and produced
		// 5-byte-varint ids, which corrupted those prefixes.)
		private const uint CreationInstanceBase = 0x02000000;

		private const uint CreationInstanceMax = 0x0FFFFFFF;

		// Slave Galley (character-creation) map. 0xFAA/4010 is the map the retail create capture's world
		// entry uses (account-stricken998-character-strickenen-create-random-actions) and it exists in the
		// seeded Maps table; 0xFA0/4000 does NOT exist and caused a Map foreign-key failure on save.
		private const uint CreationMapId = 0x00000FAA;

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

			// Only reuse an in-range unnamed row; an out-of-range leftover (e.g. created before the
			// 4-byte-varint id scheme) would yield a 5-byte instance, so drop those and allocate fresh.
			var character = await ctx.Characters.FirstOrDefaultAsync(c => c.AccountId == accountId && c.Name == null && c.Id >= CreationInstanceBase && c.Id <= CreationInstanceMax);
			if (character == null)
			{
				var stale = await ctx.Characters.Where(c => c.AccountId == accountId && c.Name == null).ToListAsync();
				if (stale.Count > 0)
				{
					ctx.Characters.RemoveRange(stale);
					await ctx.SaveChangesAsync();
				}

				character = new Character
				{
					Id = await AllocateCharacterId(ctx),
					AccountId = accountId,
					RealmId = realmId,
					AuthChallenge = Guid.NewGuid().ToString("N"),
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

		// --- ad-hoc units of work ------------------------------------------------------------------------------

		public async Task Execute(Func<IDatabaseContext, Task> work)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await work(ctx);
		}

		public async Task<Ability> GetAbility(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Abilities.FirstOrDefaultAsync(a => a.Id == id);
		}

		// The class/level row that lists which abilities (and spells) a character of this class+level should have.
		// Falls back to the highest captured level at or below the requested one so any level still resolves a grant.
		public async Task<ClassAbility> GetClassAbility(uint @class, uint level)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.ClassAbilities.Where(c => c.Class == @class && c.Level <= level).OrderByDescending(c => c.Level).FirstOrDefaultAsync();
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
			return await ctx.Characters.Include(c => c.Body).Include(c => c.MaxStats).FirstOrDefaultAsync(c => c.Id == id);
		}

		// --- equipment -------------------------------------------------------------------------------------

		public async Task<CharacterEquipment> GetCharacterEquipment(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			// Eager-load every equip-slot item nav + the bag slot so the inventory builder can read them detached.
			return await ctx.CharacterEquipment.Include(e => e.Neck).Include(e => e.Head).Include(e => e.Tokens).Include(e => e.Chest).Include(e => e.MainHandLeft).Include(e => e.MainHandRight).Include(e => e.OffHandLeft)
				.Include(e => e.OffHandRight).Include(e => e.Belt).Include(e => e.RingLeft).Include(e => e.RingRight).Include(e => e.Cloak).Include(e => e.Shoulder).Include(e => e.Boots).Include(e => e.Hands).Include(e => e.Legs)
				.Include(e => e.FirstPosBag).Include(e => e.Wrist).FirstOrDefaultAsync(e => e.CharacterId == characterId);
		}

		public async Task<Character> GetCharacterForAccount(uint accountId, uint clientInstance)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();

			var directMatch = await ctx.Characters.Include(c => c.Body).Include(c => c.MaxStats).FirstOrDefaultAsync(c => c.AccountId == accountId && c.Id == clientInstance);
			if (directMatch != null)
				return directMatch;

			return await ctx.Characters.Include(c => c.Body).Include(c => c.MaxStats).Where(c => c.AccountId == accountId).OrderByDescending(c => c.Id).FirstOrDefaultAsync();
		}

		public async Task<Character[]> GetCharactersByAccount(uint id, bool skipUninitialized = true)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var q = ctx.Characters.Where(a => a.AccountId == id);
			if (skipUninitialized)
				q = q.Where(c => c.Name != null);

			return await q.ToArrayAsync();
		}

		public async Task<ConversationNode> GetConversationNode(uint npcHandle, uint typeId, uint mode, uint field3, bool exactHandleOnly = false)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();

			// Prefer the node captured for THIS exact NPC handle - the correct per-NPC dialogue (handles are stable
			// because scenes are replayed verbatim). (TypeId, Mode, Field3) is NOT NPC-unique, so the args-only
			// match can be the wrong NPC's conversation.
			var exact = await ctx.ConversationNodes.Include(n => n.Packets).FirstOrDefaultAsync(n => n.CapturedNpcHandle == npcHandle && n.TypeId == typeId && n.Mode == mode && n.Field3 == field3);
			if (exact != null || exactHandleOnly)
				return exact;

			// Args-only fallback (the pre-handle behaviour) so a captured conversation still plays for an NPC whose
			// handle differs from the replayed scene's. Callers that can't tolerate a wrong-NPC match (the create
			// world intro, where the scripted fallback is preferable) pass exactHandleOnly.
			return await ctx.ConversationNodes.Include(n => n.Packets).Where(n => n.TypeId == typeId && n.Mode == mode && n.Field3 == field3).OrderByDescending(n => n.Packets.Count).FirstOrDefaultAsync();
		}

		// Routes a conversation by the clicked NPC's stable NAME (handles are session-volatile). Exact match on
		// (NpcName, TypeId, Mode, Field3) - the (Mode, Field3) still select the branch within that NPC's dialogue;
		// the name guarantees the RIGHT NPC, which the old handle/args-only lookup could not across captures.
		public async Task<ConversationNode> GetConversationNodeByName(string npcName, uint typeId, uint mode, uint field3)
		{
			if (string.IsNullOrEmpty(npcName))
				return null;

			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.ConversationNodes.Include(n => n.Packets).FirstOrDefaultAsync(n => n.NpcName == npcName && n.TypeId == typeId && n.Mode == mode && n.Field3 == field3);
		}

		public async Task<Item> GetItem(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Items.FirstOrDefaultAsync(i => i.Id == id);
		}

		public async Task<Map> GetMap(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Maps.FirstOrDefaultAsync(m => m.Id == id);
		}

		public async Task<Map[]> GetMaps()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Maps.OrderBy(m => m.Id).ToArrayAsync();
		}

		public async Task<MapWorldBurst> GetMapWorldBurst(uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var burst = await ctx.MapWorldBursts.Include(b => b.Packets).FirstOrDefaultAsync(b => b.MapId == mapId);

			// Guard: a burst with an unknown captured instance (0) can't be re-addressed, so treat it as absent and
			// let login fall back. Out-of-range instances ARE supported now - RewriteBurstInstance pads the live id's
			// varint to the captured length (overlong), keeping the substitution length-preserving.
			if (burst != null && burst.CapturedInstance == 0)
				return null;

			return burst;
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
			return await ctx.Realms.OrderBy(r => r.Id).ToArrayAsync();
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

		public async Task<T> Query<T>(Func<IDatabaseContext, Task<T>> work)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await work(ctx);
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
			tracked.Bag = equipment.Bag;
			tracked.Quest = equipment.Quest;
			tracked.Resources = equipment.Resources;

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

		// Allocates the next character id in the 4-byte protobuf-varint range, one past the highest existing
		// in-range id (so it stays unique without colliding with out-of-range manually-inserted rows).
		private static async Task<uint> AllocateCharacterId(DatabaseContext ctx)
		{
			var maxInRange = await ctx.Characters.Where(c => c.Id >= CreationInstanceBase && c.Id <= CreationInstanceMax).Select(c => (uint?)c.Id).MaxAsync();
			return (maxInRange ?? CreationInstanceBase - 1) + 1;
		}

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
			return ctx.CharacterEquipment.Include(e => e.Neck).Include(e => e.Head).Include(e => e.Tokens).Include(e => e.Chest).Include(e => e.MainHandLeft).Include(e => e.MainHandRight).Include(e => e.OffHandLeft)
				.Include(e => e.OffHandRight).Include(e => e.Belt).Include(e => e.RingLeft).Include(e => e.RingRight).Include(e => e.Cloak).Include(e => e.Shoulder).Include(e => e.Boots).Include(e => e.Hands).Include(e => e.Legs)
				.Include(e => e.FirstPosBag).Include(e => e.Wrist).FirstOrDefaultAsync(e => e.Id == id);
		}

		private static async Task<Item> ResolveItem(DatabaseContext ctx, uint? id)
		{
			return id == null
				? null
				: await ctx.Items.FindAsync(id.Value);
		}
	}
}
