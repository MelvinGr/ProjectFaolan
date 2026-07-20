using System;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Repositories
{
	public partial interface IDatabaseRepository
	{
		Task<Guild?> CreateGuild(string name, uint leaderCharacterId);
		Task<Guild?> GetGuild(uint id);
		Task<Guild?> GetGuildByName(string name, uint realmId);
		Task<Guild[]> GetGuilds(uint realmId);
		Task SaveGuild(Guild guild);
	}

	public partial class DatabaseRepository
	{
		public async Task<Guild?> CreateGuild(string name, uint leaderCharacterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var leader = await ctx.Characters.FirstOrDefaultAsync(c => c.Id == leaderCharacterId);
			if (leader == null || leader.GuildId != null)
				return null;

			if (await ctx.Guilds.AnyAsync(g => g.RealmId == leader.RealmId && g.Name == name))
				return null;

			var guild = new Guild
			{
				Name = name,
				RealmId = leader.RealmId,
				CreationDate = DateTimeOffset.UtcNow
			};
			ctx.Guilds.Add(guild);
			await ctx.SaveChangesAsync();

			leader.GuildId = guild.Id;
			leader.GuildRank = GuildRank.Leader;
			await ctx.SaveChangesAsync();

			return await ctx.Guilds.Include(g => g.Members).FirstOrDefaultAsync(g => g.Id == guild.Id);
		}

		public async Task<Guild?> GetGuild(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Guilds.Include(g => g.Members).FirstOrDefaultAsync(g => g.Id == id);
		}

		public async Task<Guild?> GetGuildByName(string name, uint realmId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Guilds.Include(g => g.Members).Where(g => g.RealmId == realmId && g.Name == name).FirstOrDefaultAsync();
		}

		public async Task<Guild[]> GetGuilds(uint realmId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Guilds.Include(g => g.Members).Where(g => g.RealmId == realmId).ToArrayAsync();
		}

		public async Task SaveGuild(Guild guild)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			ctx.Entry(guild).State = EntityState.Modified;
			await ctx.SaveChangesAsync();
		}
	}
}
