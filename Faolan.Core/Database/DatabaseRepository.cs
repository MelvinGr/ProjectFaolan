using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Data;
using Faolan.Core.Network;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database
{
    public interface IDatabaseRepository
    {
        IDatabaseContext Context { get; }

        Task<bool> CheckLogin(string username, string password);

        Task<bool> UpdateLastInfo(Account account, NetworkClient client);
        Task<bool> UpdateLastInfo(Character character, NetworkClient client);

        Task<bool> UpdateClientInstance(Account account, uint characterId);

        Task<Account> GetAccount(uint id);
        Task<Account> GetAccount(string userName);

        Task<Character> GetCharacter(uint id);
        Task<Character[]> GetCharactersByAccount(uint id, bool skipUninitialized = true);
        Task<Character> CreateCharacter(uint accountId, uint realmId);
        Task<bool> UpdateCharacterPosition(uint id, Vector3? position = null, Vector3? rotation = null);

        Task<Realm> GetRealm(uint id);

        Task<WorldMap> GetMap(uint id);

        Task<Spell> GetSpell(uint id);
    }

    public class DatabaseRepository : IDatabaseRepository
    {
        public DatabaseRepository(IDatabaseContext databaseContext)
        {
            Context = databaseContext;
        }

        public IDatabaseContext Context { get; }

        public async Task<bool> CheckLogin(string username, string password)
        {
            username = username.ToLower(); // skip check password for now
            return await Context.Accounts.AnyAsync(a => a.UserName.ToLower() == username);
        }

        public async Task<bool> UpdateLastInfo(Account account, NetworkClient client)
        {
            account.LastConnection = DateTime.UtcNow;
            account.LastIpAddress = client.IpAddress;
            return await Context.SaveChangesAsync() > 0;
        }

        public async Task<bool> UpdateLastInfo(Character character, NetworkClient client)
        {
            character.LastConnection = DateTime.UtcNow;
            character.LastIpAddress = client.IpAddress;
            return await Context.SaveChangesAsync() > 0;
        }

        public async Task<bool> UpdateClientInstance(Account account, uint characterId)
        {
            if (account.ClientInstance == characterId)
                return true;

            account.ClientInstance = characterId;
            return await Context.SaveChangesAsync() > 0;
        }

        public async Task<Account> GetAccount(uint id)
        {
            return await Context.Accounts.FirstOrDefaultAsync(a => a.Id == id);
        }

        public async Task<Account> GetAccount(string userName)
        {
            return await Context.Accounts.FirstOrDefaultAsync(a => a.UserName == userName);
        }

        public async Task<Character> GetCharacter(uint id)
        {
            return await Context.Characters.FirstOrDefaultAsync(a => a.Id == id);
        }

        public async Task<Character[]> GetCharactersByAccount(uint id, bool skipUninitialized = true)
        {
            var q = Context.Characters.Where(a => a.AccountId == id);
            if (skipUninitialized)
                q = q.Where(c => c.Name != null);

            return await q.ToArrayAsync();
        }

        public async Task<Character> CreateCharacter(uint accountId, uint realmId)
        {
            var character = await Context.Characters
                .FirstOrDefaultAsync(c => c.AccountId == accountId && c.Name == null);
            if (character == null)
            {
                character = new Character
                {
                    AccountId = accountId,
                    RealmId = realmId,
                    CreationDate = DateTime.UtcNow
                };

                // ReSharper disable once MethodHasAsyncOverload
                Context.Characters.Add(character);
                await Context.SaveChangesAsync();
            }

            return character;
        }

        public async Task<bool> UpdateCharacterPosition(uint id, Vector3? position = null, Vector3? rotation = null)
        {
            var character = await Context.Characters.FirstOrDefaultAsync(c => c.Id == id);
            if (character == null)
                return false;

            if (position.HasValue)
                character.Position = position.Value;

            if (rotation.HasValue)
                character.Rotation = rotation.Value;

            return await Context.SaveChangesAsync() > 0;
        }

        public async Task<Realm> GetRealm(uint id)
        {
            return await Context.Realms.FirstOrDefaultAsync(r => r.Id == id);
        }

        public async Task<WorldMap> GetMap(uint id)
        {
            return await Context.WorldMaps.FirstOrDefaultAsync(m => m.Id == id);
        }

        public async Task<Spell> GetSpell(uint id)
        {
            return await Context.Spells.FirstOrDefaultAsync(s => s.Id == id);
        }
    }
}