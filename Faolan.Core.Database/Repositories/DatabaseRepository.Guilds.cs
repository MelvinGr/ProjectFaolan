using System;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Repositories
{
	public interface IGuildRepository
	{
		Task<Guild?> CreateGuild(string name, uint leaderCharacterId);
		Task<Guild?> GetGuild(uint id);
		Task<Guild?> GetGuildByName(string name, uint realmId);
		Task<Guild[]> GetGuilds(uint realmId);

		/// <summary>Adds to the guild bank in one statement; the new balance, or null when there is no such guild.</summary>
		Task<ulong?> DepositGuildCash(uint guildId, ulong amountTin);

		/// <summary>
		///     Takes from the guild bank in one statement, only if the balance covers it; the new balance, or null when it
		///     does not (or there is no such guild).
		/// </summary>
		Task<ulong?> WithdrawGuildCash(uint guildId, ulong amountTin);

		Task SetGuildMessageOfTheDay(uint guildId, string messageOfTheDay);
	}

	public partial class DatabaseRepository
	{
		public async Task<Guild?> CreateGuild(string name, uint leaderCharacterId)
		{
			// One transaction, so the name check, the guild and the leader's membership land together: a failure
			// between the two saves left a guild with no members holding the name, and two founders of one name both
			// passed the check.
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await using var tx = await ctx.Database.BeginTransactionAsync();
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
			await tx.CommitAsync();

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

		// The guild bank moves in single statements rather than as a loaded row written back whole: two members
		// depositing at once each read the same balance, and the second whole-row save dropped the first deposit (a
		// MOTD edit racing a deposit wrote the old balance back the same way).
		public async Task<ulong?> DepositGuildCash(uint guildId, ulong amountTin)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await using var tx = await ctx.Database.BeginTransactionAsync();
			if (await ctx.Guilds.Where(g => g.Id == guildId).ExecuteUpdateAsync(s => s.SetProperty(g => g.Cash, g => g.Cash + amountTin)) != 1)
				return null;

			var balance = await ctx.Guilds.Where(g => g.Id == guildId).Select(g => g.Cash).SingleAsync();
			await tx.CommitAsync();
			return balance;
		}

		public async Task<ulong?> WithdrawGuildCash(uint guildId, ulong amountTin)
		{
			// The balance is read and compared here, then the write is conditioned on it being unchanged: SQLite cannot
			// translate an ordering comparison on a ulong column (it stores a signed 64-bit integer), only equality.
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var cash = await ctx.Guilds.Where(g => g.Id == guildId).Select(g => (ulong?)g.Cash).FirstOrDefaultAsync();
			if (cash is not { } balance || balance < amountTin)
				return null;

			return await ctx.Guilds.Where(g => g.Id == guildId && g.Cash == balance).ExecuteUpdateAsync(s => s.SetProperty(g => g.Cash, balance - amountTin)) == 1
				? balance - amountTin
				: null;
		}

		public async Task SetGuildMessageOfTheDay(uint guildId, string messageOfTheDay)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Guilds.Where(g => g.Id == guildId).ExecuteUpdateAsync(s => s.SetProperty(g => g.MessageOfTheDay, messageOfTheDay));
		}
	}
}
