using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Faolan.Core.Database
{
	public interface IDatabaseContext
	{
		DatabaseFacade Database { get; } // from DbContext
		DbSet<Ability> Abilities { get; }
		DbSet<Combo> Combos { get; }
		DbSet<Account> Accounts { get; }
		DbSet<AppearanceChoice> AppearanceChoices { get; }
		DbSet<Character> Characters { get; }
		DbSet<CharacterAttribute> CharacterAttributes { get; }
		DbSet<CharacterBody> CharacterBodies { get; }
		DbSet<CharacterBodyResource> CharacterBodyResources { get; }
		DbSet<CharacterEquipment> CharacterEquipment { get; }
		DbSet<CharacterInventory> CharacterInventory { get; }
		DbSet<CharacterMaxStats> CharacterMaxStats { get; }
		DbSet<CharacterQuest> CharacterQuests { get; }
		DbSet<CharacterRespawnPoint> CharacterRespawnPoints { get; }
		DbSet<CharacterScaffoldPair> CharacterScaffoldPairs { get; }
		DbSet<CharacterShortcut> CharacterShortcuts { get; }
		DbSet<CharacterSkill> CharacterSkills { get; }
		DbSet<CharacterStat> CharacterStats { get; }
		DbSet<CharacterAbility> CharacterAbilities { get; }
		DbSet<CharacterCombo> CharacterCombos { get; }
		DbSet<CharacterSpell> CharacterSpells { get; }
		DbSet<CharacterTag> CharacterTags { get; }
		DbSet<ChatMessage> ChatMessages { get; }
		DbSet<ClassAbility> ClassAbilities { get; }
		DbSet<ClassCombo> ClassCombos { get; }
		DbSet<ClassSpell> ClassSpells { get; }
		DbSet<ConversationNode> ConversationNodes { get; }
		DbSet<ConversationPacket> ConversationPackets { get; }
		DbSet<Guild> Guilds { get; }
		DbSet<Item> Items { get; }
		DbSet<LuaScript> LuaScripts { get; }
		DbSet<Mail> Mails { get; }
		DbSet<MailAttachment> MailAttachments { get; }
		DbSet<Map> Maps { get; }
		DbSet<MapLootEntry> MapLootEntries { get; }
		DbSet<MorphTarget> MorphTargets { get; }
		DbSet<Npc> Npcs { get; }
		DbSet<NpcSpawn> NpcSpawns { get; }
		DbSet<NpcSpawnEquipment> NpcSpawnEquipment { get; }
		DbSet<NpcSpawnMorph> NpcSpawnMorphs { get; }
		DbSet<NpcSpell> NpcSpells { get; }
		DbSet<PatrolPath> PatrolPaths { get; }
		DbSet<PatrolWaypoint> PatrolWaypoints { get; }
		DbSet<QuestDefinition> QuestDefinitions { get; }
		DbSet<QuestGoalDefinition> QuestGoalDefinitions { get; }
		DbSet<QuestTextRef> QuestTextRefs { get; }
		DbSet<Realm> Realms { get; }
		DbSet<RespawnPoint> RespawnPoints { get; }
		DbSet<Shop> Shops { get; }
		DbSet<ShopItem> ShopItems { get; }
		DbSet<Spell> Spells { get; }
		DbSet<TradepostListing> TradepostListings { get; }
		DbSet<WorldObject> WorldObjects { get; }
		Task<int> SaveChangesAsync(CancellationToken cancellationToken = default); // from DbContext
	}

	public class DatabaseContext : DbContext, IDatabaseContext
	{
		public DbSet<Ability> Abilities { get; set; }
		public DbSet<Combo> Combos { get; set; }
		public DbSet<Account> Accounts { get; set; }
		public DbSet<AppearanceChoice> AppearanceChoices { get; set; }
		public DbSet<Character> Characters { get; set; }

		public DbSet<CharacterAttribute> CharacterAttributes { get; set; }
		public DbSet<CharacterBody> CharacterBodies { get; set; }
		public DbSet<CharacterBodyResource> CharacterBodyResources { get; set; }
		public DbSet<CharacterEquipment> CharacterEquipment { get; set; }
		public DbSet<CharacterInventory> CharacterInventory { get; set; }

		public DbSet<CharacterMaxStats> CharacterMaxStats { get; set; }
		public DbSet<CharacterQuest> CharacterQuests { get; set; }
		public DbSet<CharacterRespawnPoint> CharacterRespawnPoints { get; set; }
		public DbSet<CharacterScaffoldPair> CharacterScaffoldPairs { get; set; }
		public DbSet<CharacterShortcut> CharacterShortcuts { get; set; }
		public DbSet<CharacterSkill> CharacterSkills { get; set; }
		public DbSet<CharacterStat> CharacterStats { get; set; }
		public DbSet<CharacterAbility> CharacterAbilities { get; set; }
		public DbSet<CharacterCombo> CharacterCombos { get; set; }
		public DbSet<CharacterSpell> CharacterSpells { get; set; }
		public DbSet<CharacterTag> CharacterTags { get; set; }
		public DbSet<ChatMessage> ChatMessages { get; set; }
		public DbSet<ClassAbility> ClassAbilities { get; set; }
		public DbSet<ClassCombo> ClassCombos { get; set; }
		public DbSet<ClassSpell> ClassSpells { get; set; }
		public DbSet<ConversationNode> ConversationNodes { get; set; }
		public DbSet<ConversationPacket> ConversationPackets { get; set; }
		public DbSet<Guild> Guilds { get; set; }
		public DbSet<Item> Items { get; set; }
		public DbSet<LuaScript> LuaScripts { get; set; }
		public DbSet<Mail> Mails { get; set; }
		public DbSet<MailAttachment> MailAttachments { get; set; }
		public DbSet<Map> Maps { get; set; }
		public DbSet<MapLootEntry> MapLootEntries { get; set; }
		public DbSet<MorphTarget> MorphTargets { get; set; }
		public DbSet<Npc> Npcs { get; set; }
		public DbSet<NpcSpawn> NpcSpawns { get; set; }
		public DbSet<NpcSpawnEquipment> NpcSpawnEquipment { get; set; }
		public DbSet<NpcSpawnMorph> NpcSpawnMorphs { get; set; }
		public DbSet<NpcSpell> NpcSpells { get; set; }
		public DbSet<PatrolPath> PatrolPaths { get; set; }
		public DbSet<PatrolWaypoint> PatrolWaypoints { get; set; }
		public DbSet<QuestDefinition> QuestDefinitions { get; set; }
		public DbSet<QuestGoalDefinition> QuestGoalDefinitions { get; set; }
		public DbSet<QuestTextRef> QuestTextRefs { get; set; }
		public DbSet<Realm> Realms { get; set; }
		public DbSet<RespawnPoint> RespawnPoints { get; set; }
		public DbSet<Shop> Shops { get; set; }
		public DbSet<ShopItem> ShopItems { get; set; }
		public DbSet<Spell> Spells { get; set; }
		public DbSet<TradepostListing> TradepostListings { get; set; }
		public DbSet<WorldObject> WorldObjects { get; set; }

		public DatabaseContext(DbContextOptions<DatabaseContext> dbContextOptions) : base(dbContextOptions)
		{
			//
		}

		// No OnConfiguring: the context is created short-lived from a pooled DbContextFactory (one per persistence
		// unit of work), so lazy-loading proxies are deliberately NOT used - they tether entities to a live context
		// and break once it is disposed. Navigations are loaded explicitly with Include where needed.

		protected override void OnModelCreating(ModelBuilder modelBuilder)
		{
			// Enum columns marked [ConstrainToEnumValues] get a generated CHECK constraint (the allowed list is built from
			// the enum, so it stays in sync). CharacterStatId is intentionally left open (Unknown fillers for undecoded ids).
			ConstrainToEnumValuesAttribute.ApplyEnumCheckConstraints(modelBuilder);

			// Natural-key catalogs whose Id is an external id set by the importer (RDB/wire id), never DB-generated - so
			// EF must not treat the integer PK as auto-increment (like Character/RespawnPoint/MorphTarget below).
			modelBuilder.Entity<Ability>().Property(a => a.Id).ValueGeneratedNever();
			modelBuilder.Entity<Spell>().Property(s => s.Id).ValueGeneratedNever();
			modelBuilder.Entity<Combo>().Property(c => c.Id).ValueGeneratedNever();
			modelBuilder.Entity<Item>().Property(i => i.Id).ValueGeneratedNever();
			modelBuilder.Entity<Map>().Property(m => m.Id).ValueGeneratedNever();
			modelBuilder.Entity<LuaScript>().Property(l => l.Id).ValueGeneratedNever();

			modelBuilder.Entity<Character>().HasOne(c => c.Account).WithMany(r => r.Characters).OnDelete(DeleteBehavior.Cascade);
			// MapId/RealmId are required, so SetNull is no longer possible - and cascading would delete
			// characters with their map/realm; a map or realm with characters cannot be deleted instead.
			modelBuilder.Entity<Character>().HasOne(c => c.Realm).WithMany(r => r.Characters).OnDelete(DeleteBehavior.Restrict);
			modelBuilder.Entity<Character>().HasOne(c => c.Map).WithMany().OnDelete(DeleteBehavior.Restrict);

			// Character ids double as the on-wire client instance. They MUST stay <= 0x0FFFFFFF (4-byte protobuf
			// varint): the world-entry burst is still REPLAYED with instance SUBSTITUTION (RewriteBurstInstance pads
			// the live id into the captured 4-byte varint slot via EncodeVarintPadded) - it is not fully rebuilt - so a
			// 5-byte id (> 2^28) can't substitute, leaving the captured instance in the burst and crashing the client.
			// SQLite max+1 alone is NOT safe: a high-id captured character imported from a 5-byte-instance capture (e.g.
			// 0x110099BD) poisons the sequence. So DatabaseRepository.CreateCharacter allocates the id explicitly from
			// the in-range max; the id is settable (not DB-generated).
			modelBuilder.Entity<Character>().Property(c => c.Id).ValueGeneratedNever();

			// RespawnPoint.Id is the client RDB resurrection-point Guid (the spawnPointId the 0x2024 release screen
			// sends + the client echoes in RespawnMe), so it must be the real RDB value, not EF-generated (#214).
			modelBuilder.Entity<RespawnPoint>().Property(r => r.Id).ValueGeneratedNever();

			// ItemType.Normal (104) is the default classification - existing item rows backfill to Normal when the
			// column is added; the importer reclassifies quest/resource items by the container they were seen in.
			// The enum has no 0 member, so the CLR default 0 is a safe sentinel: an Item left at Type==0 (e.g. a
			// .additem-created row) gets the DB default Normal, never a literal 0. Declaring the sentinel explicitly
			// confirms that intent to EF and silences the model-validation warning.
			modelBuilder.Entity<Item>().Property(i => i.Type).HasDefaultValue(ItemType.Normal).HasSentinel(0);

			// Every Tradepost (AH) listing is filed under a real catalog Item (FK ItemId -> Items.Id). Restrict: an
			// item referenced by a listing can't be deleted out from under it. Guards the #212 placeholder-ItemId import
			// bug (listings with a bogus ItemId=30000 that the client collapsed into one "Torn Rags" row).
			modelBuilder.Entity<TradepostListing>().HasOne(t => t.Item).WithMany().HasForeignKey(t => t.ItemId).OnDelete(DeleteBehavior.Restrict);

			// One-to-one Character <-> CharacterMaxStats, keyed by CharacterId; deletes cascade with the character.
			modelBuilder.Entity<Character>().HasOne(c => c.MaxStats).WithOne(s => s.Character).HasForeignKey<CharacterMaxStats>(s => s.CharacterId).OnDelete(DeleteBehavior.Cascade);
			modelBuilder.Entity<Character>().HasMany(c => c.Inventory).WithOne(i => i.Character).HasForeignKey(i => i.CharacterId).OnDelete(DeleteBehavior.Cascade);
			modelBuilder.Entity<Character>().HasMany(c => c.Attributes).WithOne(a => a.Character).HasForeignKey(a => a.CharacterId).OnDelete(DeleteBehavior.Cascade);
			modelBuilder.Entity<Character>().HasMany(c => c.Skills).WithOne(s => s.Character).HasForeignKey(s => s.CharacterId).OnDelete(DeleteBehavior.Cascade);
			modelBuilder.Entity<Character>().HasMany(c => c.ScaffoldPairs).WithOne(s => s.Character).HasForeignKey(s => s.CharacterId).OnDelete(DeleteBehavior.Cascade);
			modelBuilder.Entity<Character>().HasMany(c => c.BodyResources).WithOne(r => r.Character).HasForeignKey(r => r.CharacterId).OnDelete(DeleteBehavior.Cascade);

			// Hotbar shortcuts delete with the character.
			modelBuilder.Entity<CharacterShortcut>().HasOne(s => s.Character).WithMany(c => c.Shortcuts).OnDelete(DeleteBehavior.Cascade);

			// Quest state, quest/dialogue tags and discovered respawn points all delete with the character.
			modelBuilder.Entity<Character>().HasMany(c => c.Quests).WithOne(q => q.Character).HasForeignKey(q => q.CharacterId).OnDelete(DeleteBehavior.Cascade);
			modelBuilder.Entity<Character>().HasMany(c => c.Tags).WithOne(t => t.Character).HasForeignKey(t => t.CharacterId).OnDelete(DeleteBehavior.Cascade);
			modelBuilder.Entity<Character>().HasMany(c => c.RespawnPoints).WithOne(r => r.Character).HasForeignKey(r => r.CharacterId).OnDelete(DeleteBehavior.Cascade);

			// Learned spells delete with the character (a character knows a given spell at most once - now the
			// (CharacterId, SpellId) compound primary key).
			modelBuilder.Entity<CharacterSpell>().HasOne(s => s.Character).WithMany(c => c.Spells).OnDelete(DeleteBehavior.Cascade);

			// Guild membership: deleting a guild releases its members back to guildless instead of deleting them.
			modelBuilder.Entity<Character>().HasOne(c => c.Guild).WithMany(g => g.Members).OnDelete(DeleteBehavior.SetNull);

			// A shop's wares delete with the shop (the importer replaces a shop's items wholesale on re-import).
			modelBuilder.Entity<Shop>().HasMany(s => s.Items).WithOne(i => i.Shop).HasForeignKey(i => i.ShopId).OnDelete(DeleteBehavior.Cascade);

			// MorphTarget.Id IS the wire tag (the client registry's 1-based id minus 1) - assigned by the
			// importer, never generated.
			modelBuilder.Entity<MorphTarget>().Property(t => t.Id).ValueGeneratedNever();

			// A conversation node is identified by the clicked NPC + the player's TalkToNpc args. The NPC handle is
			// part of the key because (TypeId, Mode, Field3) is NOT NPC-unique: the same mode means different
			// conversations for different NPCs / quest states (e.g. mode 1 is Casilda's create intro but Kalanthes's
			// regular login dialog). Handles are stable because scenes are replayed verbatim. Packets cascade-delete.
			// Keyed by NPC NAME (stable identity), not the session-volatile handle. NpcName == nullable; SQLite treats
			// NULLs as distinct, so nodes whose NPC couldn't be named stay distinct rows.
			modelBuilder
				.Entity<ConversationNode>()
				.HasIndex(n => new
				{
					n.NpcName,
					n.TypeId,
					n.AnswerPath,
					n.ConditionScript // a screen can have quest/tag-conditioned variants under one (name,type,path)
				})
				.IsUnique();
			modelBuilder.Entity<ConversationNode>().HasMany(n => n.Packets).WithOne(p => p.Node).OnDelete(DeleteBehavior.Cascade);

			// A class<->spell access row is unique per (Class, SpellId) - now the compound primary key.
			// SpellId references the Spell catalog (the importer catalogs every harvested spell id). Restrict so a
			// spell can't be deleted out from under a class loadout.
			modelBuilder.Entity<ClassSpell>().HasOne(s => s.Spell).WithMany().HasForeignKey(s => s.SpellId).OnDelete(DeleteBehavior.Restrict);

			// A captured NPC spawn is deduped by its (map, spawn point); the entity handle is session-scoped.
			modelBuilder
				.Entity<NpcSpawn>()
				.HasIndex(n => new
				{
					n.MapId,
					n.PositionX,
					n.PositionY,
					n.PositionZ
				})
				.IsUnique();

			// A humanoid NPC's appearance/morph table (#185), ordered, cascade-deletes with its NpcSpawn.
			modelBuilder.Entity<NpcSpawn>().HasMany(n => n.Morphs).WithOne(m => m.NpcSpawn).HasForeignKey(m => m.NpcSpawnId).OnDelete(DeleteBehavior.Cascade);

			// An NPC's worn equipment (CharacterIIR f23, #185), cascade-deletes with its NpcSpawn.
			modelBuilder.Entity<NpcSpawn>().HasMany(n => n.Equipment).WithOne(e => e.NpcSpawn).HasForeignKey(e => e.NpcSpawnId).OnDelete(DeleteBehavior.Cascade);

			// The NPC TYPE table (#244): one row per TemplateId, deduping the type identity duplicated across every
			// placement. A spawn (instance) and a spell point at their type by the Npc PK (NpcId); SetNull keeps the
			// instance/spell if the type is ever removed (the type is reference data, not a cascade parent).
			modelBuilder.Entity<Npc>().HasIndex(n => n.TemplateId).IsUnique();

			modelBuilder.Entity<Npc>().HasMany(n => n.Spawns).WithOne(s => s.Npc).HasForeignKey(s => s.NpcId).OnDelete(DeleteBehavior.SetNull);

			modelBuilder.Entity<Npc>().HasMany(n => n.Spells).WithOne(s => s.Npc).HasForeignKey(s => s.NpcId).OnDelete(DeleteBehavior.SetNull);

			// One patrol route per (map, spawn point) - keyed like the NpcSpawn it belongs to, so the live MoveTask
			// builder joins them by position (#53). Its waypoints cascade-delete with the route.
			modelBuilder
				.Entity<PatrolPath>()
				.HasIndex(p => new
				{
					p.MapId,
					p.PositionX,
					p.PositionY,
					p.PositionZ
				})
				.IsUnique();
			modelBuilder.Entity<PatrolPath>().HasMany(p => p.Waypoints).WithOne(w => w.PatrolPath).HasForeignKey(w => w.PatrolPathId).OnDelete(DeleteBehavior.Cascade);

			// A captured world object is deduped by its (map, spawn point, identity): two distinct props can occupy the
			// EXACT same coordinates (#222), so the display_name identity is part of the key - position alone would
			// reject the second prop. (Identity-less classes have a null DisplayName; SQLite treats NULLs as distinct,
			// so they fall back to position-only dedup, which the importer's in-memory Merge already enforces.)
			modelBuilder
				.Entity<WorldObject>()
				.HasIndex(o => new
				{
					o.MapId,
					o.PositionX,
					o.PositionY,
					o.PositionZ,
					o.DisplayName
				})
				.IsUnique();

			// One world burst per map; its structured IIR records cascade-delete.
		}
	}
}
