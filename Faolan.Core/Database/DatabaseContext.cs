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
		DbSet<Ability> Abilities { get; }
		DbSet<Account> Accounts { get; }
		DbSet<Character> Characters { get; }
		DbSet<CharacterBody> CharacterBodies { get; }
		DbSet<CharacterEquipment> CharacterEquipment { get; }
		DbSet<CharacterMaxStats> CharacterMaxStats { get; }
		DbSet<ClassAbility> ClassAbilities { get; }
		DbSet<ConversationNode> ConversationNodes { get; }
		DbSet<ConversationPacket> ConversationPackets { get; }
		DbSet<Item> Items { get; }
		DbSet<Map> Maps { get; }
		DbSet<MapObject> MapObjects { get; }
		DbSet<MapWorldBurst> MapWorldBursts { get; }
		DbSet<MapWorldBurstEntity> MapWorldBurstEntities { get; }
		DbSet<Npc> Npcs { get; }
		DbSet<NpcBody> NpcBodies { get; }
		DbSet<NpcEquipment> NpcEquipment { get; }
		DbSet<NpcSpawn> NpcSpawns { get; }
		DbSet<Realm> Realms { get; }
		DbSet<Spell> Spells { get; }
		Task<int> SaveChangesAsync(CancellationToken cancellationToken = default); // from DbContext
	}

	public class DatabaseContext : DbContext, IDatabaseContext
	{
		public DbSet<Ability> Abilities { get; set; }
		public DbSet<Account> Accounts { get; set; }
		public DbSet<Character> Characters { get; set; }
		public DbSet<CharacterBody> CharacterBodies { get; set; }

		public DbSet<CharacterEquipment> CharacterEquipment { get; set; }

		public DbSet<CharacterMaxStats> CharacterMaxStats { get; set; }
		public DbSet<ClassAbility> ClassAbilities { get; set; }
		public DbSet<ConversationNode> ConversationNodes { get; set; }
		public DbSet<ConversationPacket> ConversationPackets { get; set; }
		public DbSet<Item> Items { get; set; }
		public DbSet<Map> Maps { get; set; }
		public DbSet<MapObject> MapObjects { get; set; }
		public DbSet<MapWorldBurst> MapWorldBursts { get; set; }
		public DbSet<MapWorldBurstEntity> MapWorldBurstEntities { get; set; }
		public DbSet<Npc> Npcs { get; set; }
		public DbSet<NpcBody> NpcBodies { get; set; }
		public DbSet<NpcEquipment> NpcEquipment { get; set; }
		public DbSet<NpcSpawn> NpcSpawns { get; set; }
		public DbSet<Realm> Realms { get; set; }
		public DbSet<Spell> Spells { get; set; }

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

			// One-to-one Character <-> CharacterMaxStats, keyed by CharacterId; deletes cascade with the character.
			modelBuilder.Entity<Character>().HasOne(c => c.MaxStats).WithOne(s => s.Character).HasForeignKey<CharacterMaxStats>(s => s.CharacterId).OnDelete(DeleteBehavior.Cascade);

			// A conversation node is identified by the clicked NPC + the player's TalkToNpc args. The NPC handle is
			// part of the key because (TypeId, Mode, Field3) is NOT NPC-unique: the same mode means different
			// conversations for different NPCs / quest states (e.g. mode 1 is Casilda's create intro but Kalanthes's
			// regular login dialog). Handles are stable because scenes are replayed verbatim. Packets cascade-delete.
			// Keyed by NPC NAME (stable identity), not the session-volatile handle. NpcName is nullable; SQLite treats
			// NULLs as distinct, so nodes whose NPC couldn't be named stay distinct rows.
			modelBuilder.Entity<ConversationNode>().HasIndex(n => new
			{
				n.NpcName,
				n.TypeId,
				n.Mode,
				n.Field3
			}).IsUnique();
			modelBuilder.Entity<ConversationNode>().HasMany(n => n.Packets).WithOne(p => p.Node).OnDelete(DeleteBehavior.Cascade);

			modelBuilder.Entity<ClassAbility>().HasIndex(c => new
			{
				c.Class,
				c.Level
			}).IsUnique();

			// A captured NPC spawn is deduped by its (map, spawn point); the entity handle is session-scoped.
			modelBuilder.Entity<NpcSpawn>().HasIndex(n => new
			{
				n.MapId,
				n.PositionX,
				n.PositionY,
				n.PositionZ
			}).IsUnique();

			// One world burst per map; its structured IIR records cascade-delete.
			modelBuilder.Entity<MapWorldBurst>().HasIndex(b => b.MapId).IsUnique();
			modelBuilder.Entity<MapWorldBurst>().HasMany(b => b.Entities).WithOne(e => e.Burst).OnDelete(DeleteBehavior.Cascade);
		}
	}
}
