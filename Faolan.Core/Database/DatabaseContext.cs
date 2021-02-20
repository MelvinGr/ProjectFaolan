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
        DbSet<WorldMap> WorldMaps { get; }
        DbSet<WorldObject> WorldObjects { get; }

        DatabaseFacade Database { get; }
        Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
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
        public DbSet<WorldMap> WorldMaps { get; set; }
        public DbSet<WorldObject> WorldObjects { get; set; }

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
                .HasIndex(p => p.Name)
                .IsUnique();
        }
    }
}