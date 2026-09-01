using System;
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

		/// <summary>
		///     Stamp this realm's heartbeat - "a host is still serving me" (#922). Writes ONLY this realm's row.
		/// </summary>
		Task<bool> TouchRealm(uint realmId);
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

		// ⭐ Going ONLINE stamps the heartbeat in the same write. Otherwise there is a window between the flip and the
		// first timer tick where the row says Online with a stale (or null) LastSeenUtc, which a reader is obliged to
		// treat as down (#922) - the realm would vanish from the list for up to one interval each time it starts.
		// Going OFFLINE deliberately does NOT clear the stamp: the flag already says down, and keeping the last-seen
		// time is the more informative record of when it was last up.
		public async Task<bool> SetRealmOnlineStatus(uint realmId, RealmOnlineStatus status)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var now = DateTimeOffset.UtcNow;

			return await ctx.Realms.Where(r => r.Id == realmId).ExecuteUpdateAsync(s => s
				.SetProperty(r => r.OnlineStatus, status)
				.SetProperty(r => r.LastSeenUtc, r => status == RealmOnlineStatus.Online ? now : r.LastSeenUtc)) > 0;
		}

		public async Task<bool> TouchRealm(uint realmId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var now = DateTimeOffset.UtcNow;

			return await ctx.Realms.Where(r => r.Id == realmId).ExecuteUpdateAsync(s => s.SetProperty(r => r.LastSeenUtc, now)) > 0;
		}
	}
}
