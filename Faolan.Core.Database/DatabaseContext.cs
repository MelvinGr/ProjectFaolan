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
		DbSet<Feat> Feats { get; }
		DbSet<FeatEffect> FeatEffects { get; }
		DbSet<FeatSpellModifier> FeatSpellModifiers { get; }
		DbSet<FeatSpellStatModifier> FeatSpellStatModifiers { get; }
		DbSet<FeatSpellGrant> FeatSpellGrants { get; }
		DbSet<FeatComboModifier> FeatComboModifiers { get; }
		DbSet<FeatProc> FeatProcs { get; }
		DbSet<FeatIdMap> FeatIdMaps { get; }
		DbSet<FeatPrereq> FeatPrereqs { get; }
		DbSet<Account> Accounts { get; }
		DbSet<AccountClaim> AccountClaims { get; }
		DbSet<AppearanceChoice> AppearanceChoices { get; }

		DbSet<AppearanceSexOption> AppearanceSexOptions { get; }
		DbSet<Character> Characters { get; }
		DbSet<CharacterAbility> CharacterAbilities { get; }
		DbSet<CharacterBody> CharacterBodies { get; }
		DbSet<CharacterBodyResource> CharacterBodyResources { get; }
		DbSet<CharacterBuff> CharacterBuffs { get; }
		DbSet<CharacterClientSettings> CharacterClientSettings { get; }
		DbSet<CharacterCombo> CharacterCombos { get; }
		DbSet<CharacterEquipment> CharacterEquipment { get; }
		DbSet<CharacterFeat> CharacterFeats { get; }
		DbSet<CharacterFeatSpec> CharacterFeatSpecs { get; }
		DbSet<CharacterInventory> CharacterInventory { get; }
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
		DbSet<ClassComboScaling> ClassComboScalings { get; }
		DbSet<ClassSpell> ClassSpells { get; }
		DbSet<Combo> Combos { get; }
		DbSet<ConversationEdge> ConversationEdges { get; }
		DbSet<ConversationEntry> ConversationEntries { get; }
		DbSet<ConversationNode> ConversationNodes { get; }
		DbSet<ConversationPacket> ConversationPackets { get; }
		DbSet<ConversationScreen> ConversationScreens { get; }
		DbSet<ConversationScreenParameter> ConversationScreenParameters { get; }
		DbSet<Guild> Guilds { get; }
		DbSet<Item> Items { get; }
		DbSet<ClassAttributeBase> ClassAttributeBases { get; }
		DbSet<ItemStatModifier> ItemStatModifiers { get; }
		DbSet<Mail> Mails { get; }
		DbSet<MailAttachment> MailAttachments { get; }
		DbSet<Map> Maps { get; }
		DbSet<MapArea> MapAreas { get; }
		DbSet<MapHazard> MapHazards { get; }
		DbSet<MapRegion> MapRegions { get; }
		DbSet<MapRegionVertex> MapRegionVertices { get; }
		DbSet<TravelDestination> TravelDestinations { get; }
		DbSet<MapLootEntry> MapLootEntries { get; }
		DbSet<MorphTarget> MorphTargets { get; }
		DbSet<Mount> Mounts { get; }
		DbSet<NpcChatter> NpcChatter { get; }
		DbSet<NpcChatterParameter> NpcChatterParameter { get; }
		DbSet<NpcAmbush> NpcAmbushes { get; }
		DbSet<NpcEncounter> NpcEncounters { get; }
		DbSet<NpcEquipment> NpcEquipment { get; }
		DbSet<NpcMorph> NpcMorphs { get; }
		DbSet<NpcSpawn> NpcSpawns { get; }
		DbSet<NpcSpawnArea> NpcSpawnAreas { get; }
		DbSet<NpcSpawnAreaPoint> NpcSpawnAreaPoints { get; }
		DbSet<NpcSpawnEquipment> NpcSpawnEquipment { get; }
		DbSet<NpcSpawnMorph> NpcSpawnMorphs { get; }
		DbSet<NpcSpell> NpcSpells { get; }
		DbSet<NpcStatCore> NpcStatCores { get; }
		DbSet<NpcTemplate> NpcTemplates { get; }
		DbSet<PatrolNode> PatrolNodes { get; }
		DbSet<PatrolNodeWaypoint> PatrolNodeWaypoints { get; }
		DbSet<PatrolNodeWord> PatrolNodeWords { get; }
		DbSet<WorldObjectWord> WorldObjectWords { get; }
		DbSet<QuestLine> QuestLines { get; }
		DbSet<QuestLineReward> QuestLineRewards { get; }
		DbSet<QuestDefinition> QuestDefinitions { get; }
		DbSet<QuestGoalDefinition> QuestGoalDefinitions { get; }
		DbSet<QuestTextRef> QuestTextRefs { get; }
		DbSet<Realm> Realms { get; }
		DbSet<Recipe> Recipes { get; }
		DbSet<RecipeItem> RecipeItems { get; }
		DbSet<RespawnPoint> RespawnPoints { get; }
		DbSet<CharacterFriend> CharacterFriends { get; }
		DbSet<Shop> Shops { get; }
		DbSet<ShopItem> ShopItems { get; }
		DbSet<Spell> Spells { get; }
		DbSet<SpellEffect> SpellEffects { get; }
		DbSet<SpellStatEffect> SpellStatEffects { get; }
		DbSet<StockPhrase> StockPhrases { get; }
		DbSet<TradepostListing> TradepostListings { get; }
		DbSet<WorldObject> WorldObjects { get; }

		Task<int> SaveChangesAsync(CancellationToken cancellationToken = default); // from DbContext
	}

	public class DatabaseContext : DbContext, IDatabaseContext
	{
		public DbSet<Ability> Abilities { get; set; }
		public DbSet<Feat> Feats { get; set; }
		public DbSet<FeatEffect> FeatEffects { get; set; }
		public DbSet<FeatSpellModifier> FeatSpellModifiers { get; set; }
		public DbSet<FeatSpellStatModifier> FeatSpellStatModifiers { get; set; }
		public DbSet<FeatSpellGrant> FeatSpellGrants { get; set; }
		public DbSet<FeatComboModifier> FeatComboModifiers { get; set; }
		public DbSet<FeatProc> FeatProcs { get; set; }
		public DbSet<FeatIdMap> FeatIdMaps { get; set; }
		public DbSet<FeatPrereq> FeatPrereqs { get; set; }
		public DbSet<Account> Accounts { get; set; }
		public DbSet<AccountClaim> AccountClaims { get; set; }
		public DbSet<AppearanceChoice> AppearanceChoices { get; set; }

		public DbSet<AppearanceSexOption> AppearanceSexOptions { get; set; }
		public DbSet<Character> Characters { get; set; }
		public DbSet<CharacterAbility> CharacterAbilities { get; set; }
		public DbSet<CharacterBody> CharacterBodies { get; set; }
		public DbSet<CharacterBodyResource> CharacterBodyResources { get; set; }
		public DbSet<CharacterBuff> CharacterBuffs { get; set; }
		public DbSet<CharacterClientSettings> CharacterClientSettings { get; set; }
		public DbSet<CharacterCombo> CharacterCombos { get; set; }
		public DbSet<CharacterEquipment> CharacterEquipment { get; set; }
		public DbSet<CharacterFeat> CharacterFeats { get; set; }
		public DbSet<CharacterFeatSpec> CharacterFeatSpecs { get; set; }
		public DbSet<CharacterInventory> CharacterInventory { get; set; }
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
		public DbSet<ClassComboScaling> ClassComboScalings { get; set; }
		public DbSet<ClassSpell> ClassSpells { get; set; }
		public DbSet<Combo> Combos { get; set; }
		public DbSet<ConversationEdge> ConversationEdges { get; set; }
		public DbSet<ConversationEntry> ConversationEntries { get; set; }
		public DbSet<ConversationNode> ConversationNodes { get; set; }
		public DbSet<ConversationPacket> ConversationPackets { get; set; }
		public DbSet<ConversationScreen> ConversationScreens { get; set; }
		public DbSet<ConversationScreenParameter> ConversationScreenParameters { get; set; }
		public DbSet<Guild> Guilds { get; set; }
		public DbSet<Item> Items { get; set; }
		public DbSet<ClassAttributeBase> ClassAttributeBases { get; set; }
		public DbSet<ItemStatModifier> ItemStatModifiers { get; set; }
		public DbSet<Mail> Mails { get; set; }
		public DbSet<MailAttachment> MailAttachments { get; set; }
		public DbSet<Map> Maps { get; set; }
		public DbSet<MapArea> MapAreas { get; set; }
		public DbSet<MapHazard> MapHazards { get; set; }
		public DbSet<MapRegion> MapRegions { get; set; }
		public DbSet<MapRegionVertex> MapRegionVertices { get; set; }
		public DbSet<TravelDestination> TravelDestinations { get; set; }
		public DbSet<MapLootEntry> MapLootEntries { get; set; }
		public DbSet<MorphTarget> MorphTargets { get; set; }
		public DbSet<Mount> Mounts { get; set; }
		public DbSet<NpcChatter> NpcChatter { get; set; }
		public DbSet<NpcChatterParameter> NpcChatterParameter { get; set; }
		public DbSet<NpcAmbush> NpcAmbushes { get; set; }
		public DbSet<NpcEncounter> NpcEncounters { get; set; }
		public DbSet<NpcEquipment> NpcEquipment { get; set; }
		public DbSet<NpcMorph> NpcMorphs { get; set; }
		public DbSet<NpcSpawn> NpcSpawns { get; set; }
		public DbSet<NpcSpawnArea> NpcSpawnAreas { get; set; }
		public DbSet<NpcSpawnAreaPoint> NpcSpawnAreaPoints { get; set; }
		public DbSet<NpcSpawnEquipment> NpcSpawnEquipment { get; set; }
		public DbSet<NpcSpawnMorph> NpcSpawnMorphs { get; set; }
		public DbSet<NpcSpell> NpcSpells { get; set; }
		public DbSet<NpcStatCore> NpcStatCores { get; set; }
		public DbSet<NpcTemplate> NpcTemplates { get; set; }
		public DbSet<PatrolNode> PatrolNodes { get; set; }
		public DbSet<PatrolNodeWaypoint> PatrolNodeWaypoints { get; set; }
		public DbSet<PatrolNodeWord> PatrolNodeWords { get; set; }
		public DbSet<WorldObjectWord> WorldObjectWords { get; set; }
		public DbSet<QuestLine> QuestLines { get; set; }
		public DbSet<QuestLineReward> QuestLineRewards { get; set; }
		public DbSet<QuestDefinition> QuestDefinitions { get; set; }
		public DbSet<QuestGoalDefinition> QuestGoalDefinitions { get; set; }
		public DbSet<QuestTextRef> QuestTextRefs { get; set; }
		public DbSet<Realm> Realms { get; set; }
		public DbSet<Recipe> Recipes { get; set; }
		public DbSet<RecipeItem> RecipeItems { get; set; }
		public DbSet<RespawnPoint> RespawnPoints { get; set; }
		public DbSet<CharacterFriend> CharacterFriends { get; set; }
		public DbSet<Shop> Shops { get; set; }
		public DbSet<ShopItem> ShopItems { get; set; }
		public DbSet<Spell> Spells { get; set; }
		public DbSet<SpellEffect> SpellEffects { get; set; }
		public DbSet<SpellStatEffect> SpellStatEffects { get; set; }
		public DbSet<StockPhrase> StockPhrases { get; set; }
		public DbSet<TradepostListing> TradepostListings { get; set; }
		public DbSet<WorldObject> WorldObjects { get; set; }

		public DatabaseContext(DbContextOptions<DatabaseContext> dbContextOptions) : base(dbContextOptions)
		{
			//
		}

		protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
		{
			base.ConfigureConventions(configurationBuilder);

			// PostgreSQL has no unsigned integer types and Npgsql refuses to map uint/ulong (it would pick oid/xid8),
			// so store them as signed wider types: uint -> bigint, ulong -> numeric(20) (holds the full u64 range).
			// The model uses uint for every id (BaseEntity.Id) and ulong for money/renown/wire-ids. This runs ONLY on
			// Npgsql - EF caches the model per provider - so the SQLite schema (the committed faolan.db seed) is left
			// byte-for-byte unchanged, where uint/ulong already store as INTEGER natively.
			if (Database.IsNpgsql())
			{
				configurationBuilder.Properties<uint>().HaveConversion<long>();
				configurationBuilder.Properties<ulong>().HaveConversion<decimal>().HavePrecision(20, 0);
			}
		}

		// No OnConfiguring: the context is created short-lived from a pooled DbContextFactory (one per persistence
		// unit of work), so lazy-loading proxies are deliberately NOT used - they tether entities to a live context
		// and break once it is disposed. Navigations are loaded explicitly with Include where needed.

		// Stamp every NEW IDerivedKey row with the id its identity computes to, so what lands in the database always
		// matches ComputeId even if the caller never set it. Stamping can never be wrong - the id is a pure function
		// of columns the row already carries - and DatabaseConventionTests gates the shipped data on the two
		// agreeing. Added only: an existing row keeps its key. The importers upsert by IDENTITY, so a tracked row's
		// identity columns never change under it; a different identity IS a different row.
		//
		// This CANNOT be the only guard: these tables have no store-generated key, so an unset Id is 0, and two new
		// rows both at 0 collide on the change tracker's identity map at Add - well before SaveChanges. That is a
		// loud failure rather than a silent one, which is why it is acceptable; a caller adding several rows at once
		// (the importers, the test fixtures) sets the id itself. What this backstop covers is the single-row case
		// and any row whose identity columns were filled in after it was added.
		public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
		{
			StampDerivedKeys();
			return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
		}

		public override int SaveChanges(bool acceptAllChangesOnSuccess)
		{
			StampDerivedKeys();
			return base.SaveChanges(acceptAllChangesOnSuccess);
		}

		private void StampDerivedKeys()
		{
			foreach (var entry in ChangeTracker.Entries<IDerivedKey>())
			{
				if (entry.State == EntityState.Added)
					entry.Entity.Id = entry.Entity.ComputeId();
			}
		}

		protected override void OnModelCreating(ModelBuilder modelBuilder)
		{
			// Enum columns marked [ConstrainToEnumValues] get a generated CHECK constraint (the allowed list is built from
			// the enum, so it stays in sync). CharacterStatId is intentionally left OPEN (Unknown fillers for undecoded
			// ids) and so must never carry the attribute - a rule that was stated here and on the attribute and
			// violated in two tables anyway, so it is now GATED by
			// ConstrainToEnumValuesTests.NoOpenEnumColumnIsConstrained rather than restated (#452).
			ConstrainToEnumValuesAttribute.ApplyEnumCheckConstraints(modelBuilder, Database.IsNpgsql());

			// Natural-key catalogs whose Id is an external id set by the importer (RDB/wire id), never DB-generated - so
			// EF must not treat the integer PK as auto-increment (like Character/RespawnPoint/MorphTarget below).
			modelBuilder.Entity<Ability>().Property(a => a.Id).ValueGeneratedNever();
			modelBuilder.Entity<Feat>().Property(f => f.Id).ValueGeneratedNever(); // the RDB template id, the same id space the wire uses
			modelBuilder.Entity<FeatIdMap>().Property(f => f.WireFeatId).ValueGeneratedNever(); // harvested from RDB 1000042 by RdbFeatImporter (#646)
			modelBuilder.Entity<Spell>().Property(s => s.Id).ValueGeneratedNever();
			modelBuilder.Entity<Combo>().Property(c => c.Id).ValueGeneratedNever();
			modelBuilder.Entity<Item>().Property(i => i.Id).ValueGeneratedNever();
			modelBuilder.Entity<Map>().Property(m => m.Id).ValueGeneratedNever();
			modelBuilder.Entity<Recipe>().Property(r => r.Id).ValueGeneratedNever();
			modelBuilder.Entity<RecipeItem>().HasOne(ri => ri.Recipe).WithMany(r => r.Items).OnDelete(DeleteBehavior.Cascade);

			modelBuilder.Entity<Item>().OwnsOne(i => i.Key, OwnKey);
			modelBuilder.Entity<Ability>().OwnsOne(a => a.Key, OwnKey);

			// The LDB per-string decryption keys (#520) are the SAME 128-bit TEA key type, so they get the same owned
			// mapping - just prefixed, since these owners carry more than one. Columns are <Name>1..<Name>4.
			modelBuilder.Entity<QuestGoalDefinition>().OwnsOne(g => g.ObjectiveKey, k => OwnNamedKey(k, "ObjectiveKey"));
			modelBuilder.Entity<QuestGoalDefinition>().OwnsOne(g => g.TrailingKey, k => OwnNamedKey(k, "TrailingKey"));
			modelBuilder.Entity<QuestTextRef>().OwnsOne(r => r.KeyText, k => OwnNamedKey(k, "KeyText"));
			modelBuilder.Entity<QuestLine>().OwnsOne(a => a.TitleKey, k => OwnNamedKey(k, "TitleKey"));
			modelBuilder.Entity<QuestLine>().OwnsOne(a => a.MapKey, k => OwnNamedKey(k, "MapKey"));
			modelBuilder.Entity<QuestLine>().OwnsOne(a => a.RegionKey, k => OwnNamedKey(k, "RegionKey"));
			modelBuilder.Entity<ConversationScreen>().OwnsOne(c => c.Key, k => OwnNamedKey(k, "Key"));
			modelBuilder.Entity<ConversationScreenParameter>().OwnsOne(p => p.ContentKey, k => OwnNamedKey(k, "ContentKey"));
			modelBuilder.Entity<StockPhrase>().OwnsOne(p => p.FormatKey, k => OwnNamedKey(k, "FormatKey"));
			modelBuilder.Entity<NpcChatter>().OwnsOne(c => c.FormatKey, k => OwnNamedKey(k, "FormatKey"));
			modelBuilder.Entity<WorldObject>().OwnsOne(o => o.DisplayKey, k => OwnNamedKey(k, "DisplayKey"));
			modelBuilder.Entity<Map>().OwnsOne(m => m.TravelConfirmKey, k => OwnNamedKey(k, "TravelConfirmKey"));

			// The item's equipped stat bonuses (#3); cascade-delete with the item.
			modelBuilder.Entity<Item>().HasMany(i => i.StatModifiers).WithOne(m => m.Item).OnDelete(DeleteBehavior.Cascade);
			modelBuilder.Entity<Spell>().HasMany<SpellStatEffect>().WithOne(m => m.Spell).OnDelete(DeleteBehavior.Cascade); // #367
			modelBuilder.Entity<Spell>().HasMany<SpellEffect>().WithOne(e => e.Spell).OnDelete(DeleteBehavior.Cascade);

			// A mount's key IS its summon spell's id (#627), so the row goes with the spell.
			modelBuilder.Entity<Spell>().HasMany<Mount>().WithOne(m => m.Spell).OnDelete(DeleteBehavior.Cascade);
			modelBuilder.Entity<Mount>().Property(m => m.Id).ValueGeneratedNever();

			// A region's boundary polygon; cascade-delete with the region so a re-import of the RDB volumes never leaves
			// orphan vertices behind. One-to-one MapRegion <-> MapHazard (#504/#679): the geometry is the client's and
			// the behaviour is ours, so a hazard is a side table on the volume and dies with it.
			modelBuilder.Entity<MapRegion>().HasMany(r => r.Vertices).WithOne(v => v.Region).OnDelete(DeleteBehavior.Cascade);
			modelBuilder.Entity<MapRegion>().HasOne(r => r.Hazard).WithOne(h => h.Region).OnDelete(DeleteBehavior.Cascade);

			// Both ends cascade: deleting a character drops its own list AND its entries in everyone else's, so no
			// friend row can outlive either character. Fluent because no attribute expresses OnDelete, and NoAction on
			// the second FK would be needed on a provider that rejects two cascade paths to one table - SQLite and
			// Postgres both accept it.
			modelBuilder.Entity<CharacterFriend>().HasOne(f => f.Character).WithMany().OnDelete(DeleteBehavior.Cascade);
			modelBuilder.Entity<CharacterFriend>().HasOne(f => f.Friend).WithMany().OnDelete(DeleteBehavior.Cascade);
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

			// The authoritative open-world ambient spawn groups from RDB 1010506 (#332/#363), keyed by the stable
			// (playfield, group ordinal) pair. Restrict on the map FK (a map referenced by an area can't be deleted out
			// from under it); SetNull on the resolved-creature FK (removing a type reverts the area to unresolved, never
			// deletes it). The child anchor points cascade-delete with their area.
			modelBuilder.Entity<NpcSpawnArea>().HasOne(a => a.Map).WithMany().OnDelete(DeleteBehavior.Restrict);
			modelBuilder.Entity<NpcSpawnArea>().HasOne(a => a.NpcTemplate).WithMany().OnDelete(DeleteBehavior.SetNull);
			modelBuilder.Entity<NpcSpawnArea>().HasMany(a => a.Points).WithOne(p => p.Area).OnDelete(DeleteBehavior.Cascade);
			// ItemType.Normal (104) is the default classification - existing item rows backfill to Normal when the
			// column is added; the importer reclassifies quest/resource items by the container they were seen in.
			// The enum has no 0 member, so the CLR default 0 is a safe sentinel: an Item left at Type==0 (e.g. a
			// .additem-created row) gets the DB default Normal, never a literal 0. Declaring the sentinel explicitly
			// confirms that intent to EF and silences the model-validation warning.
			modelBuilder.Entity<Item>().Property(i => i.Type).HasDefaultValue(ItemType.Normal).HasSentinel(0);

			// Every Tradepost (AH) listing is filed under a real catalog Item (FK ItemId -> Items.Id). Restrict: an
			// item referenced by a listing can't be deleted out from under it. Guards the #212 placeholder-ItemId import
			// bug (listings with a bogus ItemId=30000 that the client collapsed into one "Torn Rags" row).
			modelBuilder.Entity<TradepostListing>().HasOne(t => t.Item).WithMany().OnDelete(DeleteBehavior.Restrict);

			// A shop ware references its Items row (the harvested ProtoAcgItem identity); Restrict so an item can't be
			// deleted out from under an open shop, and so an un-harvested ware fails the import loudly (empty shop bug).
			modelBuilder.Entity<ShopItem>().HasOne(si => si.Item).WithMany().OnDelete(DeleteBehavior.Restrict);

			// NPC worn equipment references its Items row by template (the quad + hash live once in Items, harvested
			// from the same spawn packet). Restrict, same rationale as the shop ware: an un-harvested worn item fails
			// the import loudly instead of rendering the NPC with missing gear.
			modelBuilder.Entity<NpcSpawnEquipment>().HasOne(e => e.Item).WithMany().OnDelete(DeleteBehavior.Restrict);
			modelBuilder.Entity<NpcEquipment>().HasOne(e => e.Item).WithMany().OnDelete(DeleteBehavior.Restrict);

			// A recipe ingredient/result references its Items row (the item's TEA key + hash live once in Items,
			// harvested from the recipe element by the recipes importer). Restrict, same rationale as the shop ware:
			// an un-harvested referenced item fails the import loudly instead of a recipe with a dangling ingredient.
			modelBuilder.Entity<RecipeItem>().HasOne(ri => ri.Item).WithMany().OnDelete(DeleteBehavior.Restrict);

			// A mail attachment / AH listing carries a concrete item INSTANCE, but the instance's TEA key is
			// per-TEMPLATE and lives once in Items; both reference it by Template (Restrict). For TradepostListing this
			// is a SECOND FK to Items distinct from the ItemId search-key FK above (ItemId is the search category, the
			// listed instance's Template usually differs). The codec reads Key1..4 through these navs; ItemHash stays a
			// row column (the captured wire hash, 0 for AH, differs from Items.Hash).
			modelBuilder.Entity<MailAttachment>().HasOne(a => a.Item).WithMany().OnDelete(DeleteBehavior.Restrict);
			modelBuilder.Entity<TradepostListing>().HasOne(t => t.TemplateItem).WithMany().OnDelete(DeleteBehavior.Restrict);

			modelBuilder.Entity<Character>().HasMany(c => c.Inventory).WithOne(i => i.Character).OnDelete(DeleteBehavior.Cascade);
			modelBuilder.Entity<Character>().HasMany(c => c.Skills).WithOne(s => s.Character).OnDelete(DeleteBehavior.Cascade);
			modelBuilder.Entity<Character>().HasMany(c => c.ScaffoldPairs).WithOne(s => s.Character).OnDelete(DeleteBehavior.Cascade);
			modelBuilder.Entity<Character>().HasMany(c => c.BodyResources).WithOne(r => r.Character).OnDelete(DeleteBehavior.Cascade);

			// The client-authored settings/preview blobs delete with the character.
			modelBuilder.Entity<CharacterClientSettings>().HasOne(s => s.Character).WithOne(c => c.ClientSettings).OnDelete(DeleteBehavior.Cascade);

			// Hotbar shortcuts delete with the character.
			modelBuilder.Entity<CharacterShortcut>().HasOne(s => s.Character).WithMany(c => c.Shortcuts).OnDelete(DeleteBehavior.Cascade);

			// Quest state, quest/dialogue tags and discovered respawn points all delete with the character.
			modelBuilder.Entity<Character>().HasMany(c => c.Quests).WithOne(q => q.Character).OnDelete(DeleteBehavior.Cascade);
			modelBuilder.Entity<Character>().HasMany(c => c.Tags).WithOne(t => t.Character).OnDelete(DeleteBehavior.Cascade);
			modelBuilder.Entity<Character>().HasMany(c => c.RespawnPoints).WithOne(r => r.Character).OnDelete(DeleteBehavior.Cascade);

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
			modelBuilder.Entity<Shop>().HasMany(s => s.Items).WithOne(i => i.Shop).OnDelete(DeleteBehavior.Cascade);

			// MorphTarget.Id IS the wire tag - the id that LEADS each RDB 1000076 record, verbatim (not offset;
			// see RdbMorphTargetImporter) - assigned by the importer, never generated.
			modelBuilder.Entity<MorphTarget>().Property(t => t.Id).ValueGeneratedNever();

			// The dialogue tree (node -> packet -> screen -> parameter) is keyed by POSITION all the way down, so a
			// re-import - the importer delete+reinserts the whole tree - reproduces the same keys instead of
			// renumbering surrogates and churning faolan.sql (see ConversationNode for the full why). The node's own
			// id is derived from its identity and never store-generated; each child's key is its parent's key plus
			// its Sequence, so every FK below the root is composite. Children cascade-delete with their parent.
			modelBuilder.Entity<ConversationNode>().Property(n => n.Id).ValueGeneratedNever();
			modelBuilder.Entity<ConversationEdge>().Property(e => e.Id).ValueGeneratedNever();
			modelBuilder.Entity<ConversationEntry>().Property(e => e.Id).ValueGeneratedNever();

			// The other DERIVED-id roots (see DerivedKey): each id is a pure function of the row's identity, so a
			// re-harvest reproduces it instead of renumbering the table and every child FK under it. Configured
			// fluently rather than by attribute only because the Id they override is inherited (Base3DEntity/
			// BaseEntity) - an attribute cannot be applied to an inherited property.
			modelBuilder.Entity<NpcSpawn>().Property(n => n.Id).ValueGeneratedNever();
			modelBuilder.Entity<NpcSpawnArea>().Property(a => a.Id).ValueGeneratedNever();
			modelBuilder.Entity<MapRegion>().Property(r => r.Id).ValueGeneratedNever();
			modelBuilder.Entity<WorldObject>().Property(o => o.Id).ValueGeneratedNever();
			modelBuilder.Entity<ConversationNode>().HasMany(n => n.Packets).WithOne(p => p.Node).OnDelete(DeleteBehavior.Cascade);
			modelBuilder.Entity<ConversationPacket>().HasMany(p => p.Screens).WithOne(s => s.Packet).OnDelete(DeleteBehavior.Cascade);
			modelBuilder.Entity<ConversationScreen>().HasMany(s => s.Parameters).WithOne(sp => sp.Screen).OnDelete(DeleteBehavior.Cascade);

			// A class<->spell access row is unique per (Class, SpellId) - now the compound primary key.
			// SpellId references the Spell catalog (the importer catalogs every harvested spell id). Restrict so a
			// spell can't be deleted out from under a class loadout.
			modelBuilder.Entity<ClassSpell>().HasOne(s => s.Spell).WithMany().OnDelete(DeleteBehavior.Restrict);

			// A humanoid NPC's appearance/morph table (#185), ordered, cascade-deletes with its NpcSpawn.
			modelBuilder.Entity<NpcSpawn>().HasMany(n => n.Morphs).WithOne(m => m.NpcSpawn).OnDelete(DeleteBehavior.Cascade);

			// An NPC's worn equipment (CharacterIIR f23, #185), cascade-deletes with its NpcSpawn.
			modelBuilder.Entity<NpcSpawn>().HasMany(n => n.Equipment).WithOne(e => e.NpcSpawn).OnDelete(DeleteBehavior.Cascade);

			// #363: a captured spawn's membership of an ambient spawn AREA. SetNull so wiping the areas (a re-import)
			// un-links these rows rather than deleting them; `.graduatenpcgroup` deletes the linked rows explicitly.
			modelBuilder.Entity<NpcSpawn>().HasOne(n => n.NpcSpawnArea).WithMany().OnDelete(DeleteBehavior.SetNull);

			// The NPC TYPE table (#244): one row per TemplateId, deduping the type identity duplicated across every
			// placement. A spawn (instance) and a spell point at their type by the NpcTemplate PK (NpcTemplateId); SetNull keeps the
			// instance/spell if the type is ever removed (the type is reference data, not a cascade parent).
			// TemplateId (the RDB NpcStats.f4 id) IS the primary key - a natural-key catalog like Item/Spell/Map, set
			// from the RDB by the importer and never DB-generated (#350).
			modelBuilder.Entity<NpcTemplate>().Property(n => n.TemplateId).ValueGeneratedNever();

			// QuestDefinition is a natural-key catalog too (#350): QuestId (the RDB quest id) IS the PK, never
			// DB-generated. Its Goals/TextRefs FK to QuestId (populated via the nav on import).
			modelBuilder.Entity<QuestDefinition>().Property(q => q.QuestId).ValueGeneratedNever();

			modelBuilder.Entity<NpcTemplate>().HasMany(n => n.Spawns).WithOne(s => s.NpcTemplate).OnDelete(DeleteBehavior.Restrict);

			modelBuilder.Entity<NpcTemplate>().HasMany(n => n.Spells).WithOne(s => s.NpcTemplate).OnDelete(DeleteBehavior.SetNull);

			// A vendor shop points at its NPC TYPE (#244) - keyed by template (unique), not the non-unique internal
			// name, so it's one-to-one (NpcTemplate.Shop). Restrict like the other NpcTemplate references: the type is
			// reference data, not a cascade parent.
			modelBuilder.Entity<Shop>().HasOne(s => s.NpcTemplate).WithOne(t => t.Shop).OnDelete(DeleteBehavior.Restrict);

			// A conversation node points at its NPC TYPE (#244) - many nodes per type, keyed by template so a same-named
			// NPC (25 share "Halen van Dummy") can't open another's dialogue. Nullable (create-flow scripted screens have
			// no captured spawn/template); Restrict, the type is reference data.
			modelBuilder.Entity<ConversationNode>().HasOne(n => n.NpcTemplate).WithMany().OnDelete(DeleteBehavior.Restrict);
			modelBuilder.Entity<ConversationEntry>().HasOne(e => e.NpcTemplate).WithMany().OnDelete(DeleteBehavior.Restrict);

			// The dialogue GRAPH (#642). An edge leaves one node and enters another, so the two ends need different
			// delete behaviour or the model has two cascade paths into the same row: the OUT end cascades (deleting a
			// screen takes its own branches with it), the IN end does NOT (an edge pointing AT a deleted screen is a
			// dangling branch the importer must resolve, not something to silently delete twice over). The importer's
			// rebuild drops edges and entries before nodes for the same reason.
			modelBuilder.Entity<ConversationNode>().HasMany(n => n.OutgoingEdges).WithOne(e => e.From).OnDelete(DeleteBehavior.Cascade);
			modelBuilder.Entity<ConversationEdge>().HasOne(e => e.To).WithMany().OnDelete(DeleteBehavior.NoAction);
			modelBuilder.Entity<ConversationEdge>().HasOne(e => e.GrantQuest).WithMany().OnDelete(DeleteBehavior.SetNull);
			modelBuilder.Entity<ConversationEntry>().HasOne(e => e.Node).WithMany().OnDelete(DeleteBehavior.Cascade);

			// SetNull rather than Cascade: losing the trigger object should degrade the conversation to "trigger
			// unknown", not delete the dialogue (see ConversationEntry.TriggerWorldObjectId).
			modelBuilder.Entity<ConversationEntry>().HasOne(e => e.TriggerWorldObject).WithMany().OnDelete(DeleteBehavior.SetNull);

			// A summon-only creature's TYPE-level equipment + appearance table (#307), cascade-deletes with its NpcTemplate type.
			modelBuilder.Entity<NpcTemplate>().HasMany(n => n.Equipment).WithOne(e => e.NpcTemplate).OnDelete(DeleteBehavior.Cascade);
			modelBuilder.Entity<NpcTemplate>().HasMany(n => n.Morphs).WithOne(m => m.NpcTemplate).OnDelete(DeleteBehavior.Cascade);

			// One-to-one NpcTemplate <-> NpcStatCore (#244), keyed by NpcTemplateId; the type-level stat core lifted off the Data blob
			// so a spawn body is constructed from typed storage, not spliced. Cascades with the type.
			modelBuilder.Entity<NpcTemplate>().HasOne(n => n.StatCore).WithOne(s => s.NpcTemplate).OnDelete(DeleteBehavior.Cascade);

			// The patrol route hangs directly off the NpcSpawn that walks it (#316) - the PatrolPath table between
			// them held nothing but the spawn id and this collection, so it was a join carrying no content.
			// The columnarized task tree (#316): all of a patrol's nodes hang off the path (cascade-delete); the tree
			// shape is the self-reference ParentPath (NoAction - every node is already deleted via its PatrolPathId, so
			// the self-ref must not add a second cascade path). Each node's waypoints cascade with it.
			modelBuilder.Entity<NpcSpawn>().HasMany(n => n.PatrolNodes).WithOne(n => n.NpcSpawn).OnDelete(DeleteBehavior.Cascade);
			modelBuilder.Entity<PatrolNode>().HasMany(n => n.Children).WithOne(n => n.Parent).OnDelete(DeleteBehavior.NoAction);
			modelBuilder.Entity<PatrolNode>().HasMany(n => n.Waypoints).WithOne(w => w.PatrolNode).OnDelete(DeleteBehavior.Cascade);
			modelBuilder.Entity<PatrolNode>().HasMany(n => n.Words).WithOne(w => w.PatrolNode).OnDelete(DeleteBehavior.Cascade);
			modelBuilder.Entity<WorldObject>().HasMany(o => o.Words).WithOne(w => w.WorldObject).OnDelete(DeleteBehavior.Cascade);
			return;

			// The 128-bit TEA DecryptionKey (ProtoAcgItem.key1) is an EF OWNED type, shared by every table that carries an
			// item/ability identity. Mapped to FLAT columns Key1..Key4 on the owner's table (no "Key_" prefix), matching
			// the wire ProtoDecryptionKey.Key1..4. Reflection-scans keep coverage.md honest; this keeps ONE definition of
			// "these four words are a key" instead of five copies of Data1..4.
			static void OwnKey<T>(OwnedNavigationBuilder<T, ProtoDecryptionKey> k) where T : class
			{
				OwnNamedKey(k, "Key");
			}

			// The same, under a chosen column prefix, for the owners that carry SEVERAL keys (a quest goal has an
			// objective key and a trailing one; a quest area has three).
			static void OwnNamedKey<T>(OwnedNavigationBuilder<T, ProtoDecryptionKey> k, string name) where T : class
			{
				k.Property(p => p.Key1).HasColumnName(name + "1");
				k.Property(p => p.Key2).HasColumnName(name + "2");
				k.Property(p => p.Key3).HasColumnName(name + "3");
				k.Property(p => p.Key4).HasColumnName(name + "4");
			}
		}
	}
}
