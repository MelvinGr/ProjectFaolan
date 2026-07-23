using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Repositories
{
	public interface IRealmRepository
	{
		Task<Realm?> GetRealm(uint id);
		Task<Realm[]> GetRealms();
		Task<ChatMessage[]> GetRecentChatMessages(int limit = 100, uint? channelId = null);
		Task<bool> SaveChatMessage(ChatMessage message);
		Task<bool> SetRealmOnlineStatus(uint realmId, RealmOnlineStatus status);
	}

	public partial class DatabaseRepository
	{
		public async Task<Realm?> GetRealm(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Realms.FirstOrDefaultAsync(r => r.Id == id);
		}

		public async Task<Realm[]> GetRealms()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Realms.OrderBy(r => r.Id).ToArrayAsync();
		}

		public async Task<ChatMessage[]> GetRecentChatMessages(int limit = 100, uint? channelId = null)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var q = ctx.ChatMessages.AsNoTracking();
			if (channelId.HasValue)
				q = q.Where(m => m.ChannelId == channelId.Value);
			return await q.OrderByDescending(m => m.Id).Take(limit).ToArrayAsync();
		}

		public async Task<bool> SaveChatMessage(ChatMessage message)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			ctx.ChatMessages.Add(message);
			return await ctx.SaveChangesAsync() > 0;
		}

		public async Task<bool> SetRealmOnlineStatus(uint realmId, RealmOnlineStatus status)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Realms.Where(r => r.Id == realmId).ExecuteUpdateAsync(s => s.SetProperty(r => r.OnlineStatus, status)) > 0;
		}
	}
}
