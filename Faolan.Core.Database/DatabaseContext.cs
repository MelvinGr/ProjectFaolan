using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Faolan.Core.Database
{
	public interface IDatabaseContext
	{
		DatabaseFacade Database { get; } // from DbContext

		DbSet<Ability> Abilities { get; }
		DbSet<Account> Accounts { get; }
		DbSet<AccountClaim> AccountClaims { get; }
		DbSet<AppearanceChoice> AppearanceChoices { get; }
		DbSet<Character> Characters { get; }
		DbSet<CharacterAbility> CharacterAbilities { get; }
		DbSet<CharacterAttribute> CharacterAttributes { get; }
		DbSet<CharacterBuff> CharacterBuffs { get; }
		DbSet<CharacterBody> CharacterBodies { get; }
		DbSet<CharacterBodyResource> CharacterBodyResources { get; }
		DbSet<CharacterCombo> CharacterCombos { get; }
		DbSet<CharacterEquipment> CharacterEquipment { get; }
		DbSet<CharacterInventory> CharacterInventory { get; }
		DbSet<CharacterMaxStats> CharacterMaxStats { get; }
		DbSet<CharacterQuest> CharacterQuests { get; }
		DbSet<CharacterQuestGoal> CharacterQuestGoals { get; }
		DbSet<CharacterRespawnPoint> CharacterRespawnPoints { get; }
		DbSet<CharacterScaffoldPair> CharacterScaffoldPairs { get; }
		DbSet<CharacterShortcut> CharacterShortcuts { get; }
		DbSet<CharacterSkill> CharacterSkills { get; }
		DbSet<CharacterSpell> CharacterSpells { get; }
		DbSet<CharacterStat> CharacterStats { get; }
		DbSet<CharacterTag> CharacterTags { get; }
		DbSet<ChatMessage> ChatMessages { get; }
		DbSet<ClassAbility> ClassAbilities { get; }
		DbSet<ClassCombo> ClassCombos { get; }
		DbSet<ClassSpell> ClassSpells { get; }
		DbSet<Combo> Combos { get; }
		DbSet<ConversationNode> ConversationNodes { get; }
		DbSet<ConversationPacket> ConversationPackets { get; }
		DbSet<ConversationScreen> ConversationScreens { get; }
		DbSet<ConversationScreenParameter> ConversationScreenParameters { get; }
		DbSet<Guild> Guilds { get; }
		DbSet<Item> Items { get; }
		DbSet<Mail> Mails { get; }
		DbSet<MailAttachment> MailAttachments { get; }
		DbSet<Map> Maps { get; }
		DbSet<MapLootEntry> MapLootEntries { get; }
		DbSet<MorphTarget> MorphTargets { get; }
		DbSet<NpcTemplate> NpcTemplates { get; }
		DbSet<NpcEquipment> NpcEquipment { get; }
		DbSet<NpcMorph> NpcMorphs { get; }
		DbSet<NpcSpawn> NpcSpawns { get; }
		DbSet<NpcSpawnEquipment> NpcSpawnEquipment { get; }
		DbSet<NpcSpawnMorph> NpcSpawnMorphs { get; }
		DbSet<NpcSpell> NpcSpells { get; }
		DbSet<NpcChatter> NpcChatter { get; }
		DbSet<NpcStatCore> NpcStatCores { get; }
		DbSet<PatrolPath> PatrolPaths { get; }
		DbSet<PatrolNode> PatrolNodes { get; }
		DbSet<PatrolNodeWaypoint> PatrolNodeWaypoints { get; }
		DbSet<QuestDefinition> QuestDefinitions { get; }
		DbSet<QuestGoalDefinition> QuestGoalDefinitions { get; }
		DbSet<QuestTextRef> QuestTextRefs { get; }
		DbSet<Realm> Realms { get; }
		DbSet<Recipe> Recipes { get; }
		DbSet<RecipeItem> RecipeItems { get; }
		DbSet<RespawnPoint> RespawnPoints { get; }
		DbSet<Shop> Shops { get; }
		DbSet<ShopItem> ShopItems { get; }
		DbSet<Spell> Spells { get; }
		DbSet<StockPhrase> StockPhrases { get; }
		DbSet<TradepostListing> TradepostListings { get; }
		DbSet<WorldObject> WorldObjects { get; }

		Task<int> SaveChangesAsync(CancellationToken cancellationToken = default); // from DbContext
	}

	public class DatabaseContext : DbContext, IDatabaseContext
	{
		public DbSet<Ability> Abilities { get; set; }
		public DbSet<Account> Accounts { get; set; }
		public DbSet<AccountClaim> AccountClaims { get; set; }
		public DbSet<AppearanceChoice> AppearanceChoices { get; set; }
		public DbSet<Character> Characters { get; set; }
		public DbSet<CharacterAbility> CharacterAbilities { get; set; }
		public DbSet<CharacterAttribute> CharacterAttributes { get; set; }
		public DbSet<CharacterBuff> CharacterBuffs { get; set; }
		public DbSet<CharacterBody> CharacterBodies { get; set; }
		public DbSet<CharacterBodyResource> CharacterBodyResources { get; set; }
		public DbSet<CharacterCombo> CharacterCombos { get; set; }
		public DbSet<CharacterEquipment> CharacterEquipment { get; set; }
		public DbSet<CharacterInventory> CharacterInventory { get; set; }
		public DbSet<CharacterMaxStats> CharacterMaxStats { get; set; }
		public DbSet<CharacterQuest> CharacterQuests { get; set; }
		public DbSet<CharacterQuestGoal> CharacterQuestGoals { get; set; }
		public DbSet<CharacterRespawnPoint> CharacterRespawnPoints { get; set; }
		public DbSet<CharacterScaffoldPair> CharacterScaffoldPairs { get; set; }
		public DbSet<CharacterShortcut> CharacterShortcuts { get; set; }
		public DbSet<CharacterSkill> CharacterSkills { get; set; }
		public DbSet<CharacterSpell> CharacterSpells { get; set; }
		public DbSet<CharacterStat> CharacterStats { get; set; }
		public DbSet<CharacterTag> CharacterTags { get; set; }
		public DbSet<ChatMessage> ChatMessages { get; set; }
		public DbSet<ClassAbility> ClassAbilities { get; set; }
		public DbSet<ClassCombo> ClassCombos { get; set; }
		public DbSet<ClassSpell> ClassSpells { get; set; }
		public DbSet<Combo> Combos { get; set; }
		public DbSet<ConversationNode> ConversationNodes { get; set; }
		public DbSet<ConversationPacket> ConversationPackets { get; set; }
		public DbSet<ConversationScreen> ConversationScreens { get; set; }
		public DbSet<ConversationScreenParameter> ConversationScreenParameters { get; set; }
		public DbSet<Guild> Guilds { get; set; }
		public DbSet<Item> Items { get; set; }
		public DbSet<Mail> Mails { get; set; }
		public DbSet<MailAttachment> MailAttachments { get; set; }
		public DbSet<Map> Maps { get; set; }
		public DbSet<MapLootEntry> MapLootEntries { get; set; }
		public DbSet<MorphTarget> MorphTargets { get; set; }
		public DbSet<NpcTemplate> NpcTemplates { get; set; }
		public DbSet<NpcEquipment> NpcEquipment { get; set; }
		public DbSet<NpcMorph> NpcMorphs { get; set; }
		public DbSet<NpcSpawn> NpcSpawns { get; set; }
		public DbSet<NpcSpawnEquipment> NpcSpawnEquipment { get; set; }
		public DbSet<NpcSpawnMorph> NpcSpawnMorphs { get; set; }
		public DbSet<NpcSpell> NpcSpells { get; set; }
		public DbSet<NpcChatter> NpcChatter { get; set; }
		public DbSet<NpcStatCore> NpcStatCores { get; set; }
		public DbSet<PatrolPath> PatrolPaths { get; set; }
		public DbSet<PatrolNode> PatrolNodes { get; set; }
		public DbSet<PatrolNodeWaypoint> PatrolNodeWaypoints { get; set; }
		public DbSet<QuestDefinition> QuestDefinitions { get; set; }
		public DbSet<QuestGoalDefinition> QuestGoalDefinitions { get; set; }
		public DbSet<QuestTextRef> QuestTextRefs { get; set; }
		public DbSet<Realm> Realms { get; set; }
		public DbSet<Recipe> Recipes { get; set; }
		public DbSet<RecipeItem> RecipeItems { get; set; }
		public DbSet<RespawnPoint> RespawnPoints { get; set; }
		public DbSet<Shop> Shops { get; set; }
		public DbSet<ShopItem> ShopItems { get; set; }
		public DbSet<Spell> Spells { get; set; }
		public DbSet<StockPhrase> StockPhrases { get; set; }
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
			modelBuilder.Entity<Recipe>().Property(r => r.Id).ValueGeneratedNever();
			modelBuilder.Entity<RecipeItem>().HasOne(ri => ri.Recipe).WithMany(r => r.Items).HasForeignKey(ri => ri.RecipeId).OnDelete(DeleteBehavior.Cascade);

			modelBuilder.Entity<Item>().OwnsOne(i => i.Key, OwnKey);
			modelBuilder.Entity<Ability>().OwnsOne(a => a.Key, OwnKey);

			modelBuilder.Entity<Character>().HasOne(c => c.Account).WithMany(r => r.Characters).OnDelete(DeleteBehavior.Cascade);
			// MapId/RealmId are required, so SetNull is no longer possible - and cascading would delete
			// characters with their map/realm; a map or realm with characters cannot be deleted instead.
			modelBuilder.Entity<Character>().HasOne(c => c.Realm).WithMany(r => r.Characters).OnDelete(DeleteBehavior.Restrict);
			modelBuilder.Entity<Character>().HasOne(c => c.Map).WithMany().OnDelete(DeleteBehavior.Restrict);

			// Character ids double as the on-wire client instance. They MUST stay <= 0x0FFFFFFF (4-byte protobuf
			// varint): the live instance is substituted into the FullChar snapshot's fixed 4-byte varint instance
			// slots (the non-Strie snapshot path), so a 5-byte id (> 2^28) can't fit the slot - it would leave the
			// wrong instance in the snapshot and crash the client.
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

			// A shop ware references its Items row (the harvested ProtoAcgItem identity); Restrict so an item can't be
			// deleted out from under an open shop, and so an un-harvested ware fails the import loudly (empty shop bug).
			modelBuilder.Entity<ShopItem>().HasOne(si => si.Item).WithMany().HasForeignKey(si => si.ItemTemplateId).OnDelete(DeleteBehavior.Restrict);

			// NPC worn equipment references its Items row by template (the quad + hash live once in Items, harvested
			// from the same spawn packet). Restrict, same rationale as the shop ware: an un-harvested worn item fails
			// the import loudly instead of rendering the NPC with missing gear.
			modelBuilder.Entity<NpcSpawnEquipment>().HasOne(e => e.Item).WithMany().HasForeignKey(e => e.Template).OnDelete(DeleteBehavior.Restrict);
			modelBuilder.Entity<NpcEquipment>().HasOne(e => e.Item).WithMany().HasForeignKey(e => e.Template).OnDelete(DeleteBehavior.Restrict);

			// A recipe ingredient/result references its Items row (the item's TEA key + hash live once in Items,
			// harvested from the recipe element by the recipes importer). Restrict, same rationale as the shop ware:
			// an un-harvested referenced item fails the import loudly instead of a recipe with a dangling ingredient.
			modelBuilder.Entity<RecipeItem>().HasOne(ri => ri.Item).WithMany().HasForeignKey(ri => ri.ItemId).OnDelete(DeleteBehavior.Restrict);

			// A mail attachment / AH listing carries a concrete item INSTANCE, but the instance's TEA key is
			// per-TEMPLATE and lives once in Items; both reference it by Template (Restrict). For TradepostListing this
			// is a SECOND FK to Items distinct from the ItemId search-key FK above (ItemId is the search category, the
			// listed instance's Template usually differs). The codec reads Key1..4 through these navs; ItemHash stays a
			// row column (the captured wire hash, 0 for AH, differs from Items.Hash).
			modelBuilder.Entity<MailAttachment>().HasOne(a => a.Item).WithMany().HasForeignKey(a => a.Template).OnDelete(DeleteBehavior.Restrict);
			modelBuilder.Entity<TradepostListing>().HasOne(t => t.TemplateItem).WithMany().HasForeignKey(t => t.Template).OnDelete(DeleteBehavior.Restrict);

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

			// Persisted self-buffs (#274) delete with the character; keyed by (CharacterId, BuffId). Unlike CharacterSpell
			// the BuffId is NOT a catalog FK - the spell BASE (BuffId >> 8) is what the Spells catalog holds, and a live
			// buff can carry an id with no persisted duration, so this is pure per-character savegame state.
			modelBuilder.Entity<CharacterBuff>().HasOne(b => b.Character).WithMany(c => c.Buffs).OnDelete(DeleteBehavior.Cascade);

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
			modelBuilder.Entity<ConversationPacket>().HasMany(p => p.Screens).WithOne(s => s.Packet).OnDelete(DeleteBehavior.Cascade);
			modelBuilder.Entity<ConversationScreen>().HasMany(s => s.Parameters).WithOne(sp => sp.Screen).OnDelete(DeleteBehavior.Cascade);

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
			// placement. A spawn (instance) and a spell point at their type by the NpcTemplate PK (NpcTemplateId); SetNull keeps the
			// instance/spell if the type is ever removed (the type is reference data, not a cascade parent).
			// TemplateId (the RDB NpcStats.f4 id) IS the primary key - a natural-key catalog like Item/Spell/Map, set
			// from the RDB by the importer and never DB-generated (#350).
			modelBuilder.Entity<NpcTemplate>().HasKey(n => n.TemplateId);
			modelBuilder.Entity<NpcTemplate>().Property(n => n.TemplateId).ValueGeneratedNever();

			// QuestDefinition is a natural-key catalog too (#350): QuestId (the RDB quest id) IS the PK, never
			// DB-generated. Its Goals/TextRefs FK to QuestId (populated via the nav on import).
			modelBuilder.Entity<QuestDefinition>().HasKey(q => q.QuestId);
			modelBuilder.Entity<QuestDefinition>().Property(q => q.QuestId).ValueGeneratedNever();

			modelBuilder.Entity<NpcTemplate>().HasMany(n => n.Spawns).WithOne(s => s.NpcTemplate).HasForeignKey(s => s.NpcTemplateId).OnDelete(DeleteBehavior.Restrict);

			modelBuilder.Entity<NpcTemplate>().HasMany(n => n.Spells).WithOne(s => s.NpcTemplate).HasForeignKey(s => s.NpcTemplateId).OnDelete(DeleteBehavior.SetNull);

			// A vendor shop points at its NPC TYPE (#244) - keyed by template (unique), not the non-unique internal
			// name, so it's one-to-one (NpcTemplate.Shop). Restrict like the other NpcTemplate references: the type is
			// reference data, not a cascade parent.
			modelBuilder.Entity<Shop>().HasOne(s => s.NpcTemplate).WithOne(t => t.Shop).HasForeignKey<Shop>(s => s.NpcTemplateId).OnDelete(DeleteBehavior.Restrict);

			// A conversation node points at its NPC TYPE (#244) - many nodes per type, keyed by template so a same-named
			// NPC (25 share "Halen van Dummy") can't open another's dialogue. Nullable (create-flow scripted screens have
			// no captured spawn/template); Restrict, the type is reference data.
			modelBuilder.Entity<ConversationNode>().HasOne(n => n.NpcTemplate).WithMany().HasForeignKey(n => n.NpcTemplateId).OnDelete(DeleteBehavior.Restrict);

			// A summon-only creature's TYPE-level equipment + appearance table (#307), cascade-deletes with its NpcTemplate type.
			modelBuilder.Entity<NpcTemplate>().HasMany(n => n.Equipment).WithOne(e => e.NpcTemplate).HasForeignKey(e => e.NpcTemplateId).OnDelete(DeleteBehavior.Cascade);
			modelBuilder.Entity<NpcTemplate>().HasMany(n => n.Morphs).WithOne(m => m.NpcTemplate).HasForeignKey(m => m.NpcTemplateId).OnDelete(DeleteBehavior.Cascade);

			// One-to-one NpcTemplate <-> NpcStatCore (#244), keyed by NpcTemplateId; the type-level stat core lifted off the Data blob
			// so a spawn body is constructed from typed storage, not spliced. Cascades with the type.
			modelBuilder.Entity<NpcTemplate>().HasOne(n => n.StatCore).WithOne(s => s.NpcTemplate).HasForeignKey<NpcStatCore>(s => s.NpcTemplateId).OnDelete(DeleteBehavior.Cascade);

			// One patrol route per NpcSpawn (#316): the FK joins a route to the NPC that walks it, so the runtime
			// looks it up via the spawn's nav instead of matching floats. Cascade-deletes with the spawn.
			modelBuilder.Entity<PatrolPath>().HasOne(p => p.NpcSpawn).WithOne(n => n.PatrolPath).HasForeignKey<PatrolPath>(p => p.NpcSpawnId).OnDelete(DeleteBehavior.Cascade);
			// The columnarized task tree (#316): all of a patrol's nodes hang off the path (cascade-delete); the tree
			// shape is the self-reference ParentId (NoAction - every node is already deleted via its PatrolPathId, so
			// the self-ref must not add a second cascade path). Each node's waypoints cascade with it.
			modelBuilder.Entity<PatrolPath>().HasMany(p => p.Nodes).WithOne(n => n.PatrolPath).HasForeignKey(n => n.PatrolPathId).OnDelete(DeleteBehavior.Cascade);
			modelBuilder.Entity<PatrolNode>().HasMany(n => n.Children).WithOne(n => n.Parent).HasForeignKey(n => n.ParentId).OnDelete(DeleteBehavior.NoAction);
			modelBuilder.Entity<PatrolNode>().HasMany(n => n.Waypoints).WithOne(w => w.PatrolNode).HasForeignKey(w => w.PatrolNodeId).OnDelete(DeleteBehavior.Cascade);
			// A waypoint is identified by its node + walk position, not a surrogate id (its FK is half the key).
			modelBuilder.Entity<PatrolNodeWaypoint>().HasKey(w => new { w.PatrolNodeId, w.Ordinal });

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

			return;

			// The 128-bit TEA DecryptionKey (ProtoAcgItem.key1) is an EF OWNED type, shared by every table that carries an
			// item/ability identity. Mapped to FLAT columns Key1..Key4 on the owner's table (no "Key_" prefix), matching
			// the wire ProtoDecryptionKey.Key1..4. Reflection-scans keep coverage.md honest; this keeps ONE definition of
			// "these four words are a key" instead of five copies of Data1..4.
			static void OwnKey<T>(OwnedNavigationBuilder<T, ProtoDecryptionKey> k) where T : class
			{
				k.Property(p => p.Key1).HasColumnName("Key1");
				k.Property(p => p.Key2).HasColumnName("Key2");
				k.Property(p => p.Key3).HasColumnName("Key3");
				k.Property(p => p.Key4).HasColumnName("Key4");
			}
		}
	}
}
