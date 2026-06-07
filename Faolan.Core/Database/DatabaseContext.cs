using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Faolan.Core.Database
{
	public interface IDatabaseContext
	{
		DatabaseFacade Database { get; } // from DbContext
		DbSet<Account> Accounts { get; }
		DbSet<Character> Characters { get; }
		DbSet<CharacterBody> CharacterBodies { get; }
		DbSet<CharacterEquipment> CharacterEquipment { get; }
		DbSet<Item> Items { get; }
		DbSet<Map> Maps { get; }
		DbSet<MapObject> MapObjects { get; }
		DbSet<Npc> Npcs { get; }
		DbSet<NpcBody> NpcBodies { get; }
		DbSet<NpcEquipment> NpcEquipment { get; }
		DbSet<Realm> Realms { get; }
		DbSet<Spell> Spells { get; }
		DbSet<ConversationNode> ConversationNodes { get; }
		DbSet<ConversationPacket> ConversationPackets { get; }
		DbSet<Ability> Abilities { get; }
		DbSet<ClassAbility> ClassAbilities { get; }
		DbSet<NpcSpawn> NpcSpawns { get; }
		DbSet<MapWorldBurst> MapWorldBursts { get; }
		DbSet<MapWorldBurstPacket> MapWorldBurstPackets { get; }
		Task<int> SaveChangesAsync(CancellationToken cancellationToken = default); // from DbContext
	}

	public class DatabaseContext : DbContext, IDatabaseContext
	{
		public DbSet<Account> Accounts { get; set; }
		public DbSet<Character> Characters { get; set; }
		public DbSet<CharacterBody> CharacterBodies { get; set; }

		public DbSet<CharacterEquipment> CharacterEquipment { get; set; }

		//public DbSet<CharacterMaxStats> CharacterMaxStats { get; set; }
		public DbSet<Item> Items { get; set; }
		public DbSet<Map> Maps { get; set; }
		public DbSet<MapObject> MapObjects { get; set; }
		public DbSet<Npc> Npcs { get; set; }
		public DbSet<NpcBody> NpcBodies { get; set; }
		public DbSet<NpcEquipment> NpcEquipment { get; set; }
		public DbSet<Realm> Realms { get; set; }
		public DbSet<Spell> Spells { get; set; }
		public DbSet<ConversationNode> ConversationNodes { get; set; }
		public DbSet<ConversationPacket> ConversationPackets { get; set; }
		public DbSet<Ability> Abilities { get; set; }
		public DbSet<ClassAbility> ClassAbilities { get; set; }
		public DbSet<NpcSpawn> NpcSpawns { get; set; }
		public DbSet<MapWorldBurst> MapWorldBursts { get; set; }
		public DbSet<MapWorldBurstPacket> MapWorldBurstPackets { get; set; }

		public DatabaseContext(DbContextOptions<DatabaseContext> dbContextOptions) : base(dbContextOptions)
		{
			//
		}

		// No OnConfiguring: the context is created short-lived from a pooled DbContextFactory (one per persistence
		// unit of work), so lazy-loading proxies are deliberately NOT used - they tether entities to a live context
		// and break once it is disposed. Navigations are loaded explicitly with Include where needed.

		protected override void OnModelCreating(ModelBuilder modelBuilder)
		{
			modelBuilder.Entity<Character>().HasOne(c => c.Account).WithMany(r => r.Characters).OnDelete(DeleteBehavior.Cascade);
			modelBuilder.Entity<Character>().HasOne(c => c.Realm).WithMany(r => r.Characters).OnDelete(DeleteBehavior.SetNull);

			// Character ids double as the on-wire client instance for the create/world flow. We assign them
			// explicitly (in the 4-byte protobuf-varint range) instead of using SQLite autoincrement, so the
			// instance is the same byte length as the capture placeholder everywhere it is substituted (header
			// varint, body raw uint32, body nested varint) - keeping the verbatim packets' length prefixes valid.
			modelBuilder.Entity<Character>().Property(c => c.Id).ValueGeneratedNever();

			// A conversation node is uniquely identified by the player's TalkToNpc args; its packets cascade-delete.
			modelBuilder.Entity<ConversationNode>().HasIndex(n => new { n.TypeId, n.Mode, n.Field3 }).IsUnique();
			modelBuilder.Entity<ConversationNode>().HasMany(n => n.Packets).WithOne(p => p.Node).OnDelete(DeleteBehavior.Cascade);

			modelBuilder.Entity<ClassAbility>().HasIndex(c => new { c.Class, c.Level }).IsUnique();

			// A captured NPC spawn is deduped by its (map, spawn point); the entity handle is session-scoped.
			modelBuilder.Entity<NpcSpawn>().HasIndex(n => new { n.MapId, n.PositionX, n.PositionY, n.PositionZ }).IsUnique();

			// One world burst per map; its packets cascade-delete.
			modelBuilder.Entity<MapWorldBurst>().HasIndex(b => b.MapId).IsUnique();
			modelBuilder.Entity<MapWorldBurst>().HasMany(b => b.Packets).WithOne(p => p.Burst).OnDelete(DeleteBehavior.Cascade);
		}
	}
}
