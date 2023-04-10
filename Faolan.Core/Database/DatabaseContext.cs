using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Faolan.Core.Database
{
	public interface IDatabaseContext
	{
		DbSet<Account> Accounts { get; }
		DbSet<Character> Characters { get; }
		DbSet<CharacterBody> CharacterBodies { get; }
		DbSet<CharacterEquipment> CharacterEquipment { get; }
		DbSet<Item> Items { get; }
		DbSet<Npc> Npcs { get; }
		DbSet<NpcBody> NpcBodies { get; }
		DbSet<NpcEquipment> NpcEquipment { get; }
		DbSet<Realm> Realms { get; }
		DbSet<Spell> Spells { get; }
		DbSet<Map> Maps { get; }
		DbSet<MapObject> MapObjects { get; }

		DatabaseFacade Database { get; } // from DbContext
		Task<int> SaveChangesAsync(CancellationToken cancellationToken = default); // from DbContext
	}

	public class DatabaseContext : DbContext, IDatabaseContext
	{
		public DatabaseContext(DbContextOptions<DatabaseContext> dbContextOptions) : base(dbContextOptions)
		{
			//
		}

		public DbSet<Account> Accounts { get; set; }
		public DbSet<Character> Characters { get; set; }
		public DbSet<CharacterBody> CharacterBodies { get; set; }
		public DbSet<CharacterEquipment> CharacterEquipment { get; set; }
		public DbSet<Item> Items { get; set; }
		public DbSet<Npc> Npcs { get; set; }
		public DbSet<NpcBody> NpcBodies { get; set; }
		public DbSet<NpcEquipment> NpcEquipment { get; set; }
		public DbSet<Realm> Realms { get; set; }
		public DbSet<Spell> Spells { get; set; }
		public DbSet<Map> Maps { get; set; }
		public DbSet<MapObject> MapObjects { get; set; }

		protected override void OnConfiguring(DbContextOptionsBuilder options)
		{
			options.UseLazyLoadingProxies();
		}

		protected override void OnModelCreating(ModelBuilder modelBuilder)
		{
			modelBuilder
				.Entity<Account>()
				.HasIndex(p => p.UserName)
				.IsUnique();

			modelBuilder
				.Entity<Character>()
				.HasIndex(c => c.Name)
				.IsUnique();

			modelBuilder
				.Entity<Character>()
				.HasOne(c => c.Realm)
				.WithMany(r => r.Characters)
				.OnDelete(DeleteBehavior.SetNull);
		}
	}
}
