using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Repositories
{
	public partial interface IDatabaseRepository
	{
		Task<int> DeleteWorldObject(uint mapId, uint capturedHandle);
		Task<RespawnPoint?> GetDefaultSpawn(uint mapId);
		Task<Map?> GetMap(uint id);
		Task<MapArea[]> GetMapAreas(uint mapId);
		Task<Item[]> GetMapLootSample(uint mapId, int count);
		Task<Map[]> GetMaps();
		Task<QuestArea[]> GetQuestAreas();
		Task<QuestDefinition?> GetQuestDefinition(uint questId);
		Task<QuestDefinition[]> GetQuestDefinitionsByArea(uint areaId);
		Task<RespawnPoint?> GetRespawnPoint(uint id);
		Task<RespawnPoint[]> GetRespawnPoints(uint mapId);
		Task<WorldObject?> GetWorldObject(uint mapId, uint captureId);
		Task<WorldObject[]> GetWorldObjects(uint mapId);
	}

	public partial class DatabaseRepository
	{
		public async Task<int> DeleteWorldObject(uint mapId, uint capturedHandle)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.WorldObjects.Where(o => o.MapId == mapId && o.CapturedHandle == capturedHandle).ExecuteDeleteAsync();
		}

		public async Task<RespawnPoint?> GetDefaultSpawn(uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var points = ctx.RespawnPoints.Where(r => r.MapId == mapId);
			return await points.FirstOrDefaultAsync(r => r.AlwaysKnown) ?? await points.FirstOrDefaultAsync();
		}

		public async Task<Map?> GetMap(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Maps.FirstOrDefaultAsync(m => m.Id == id);
		}

		// The named sub-areas of a playfield - the regions that float the area banner when a player walks in (#348).
		public async Task<MapArea[]> GetMapAreas(uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.MapAreas.Where(a => a.MapId == mapId).ToArrayAsync();
		}

		public async Task<Item[]> GetMapLootSample(uint mapId, int count)
		{
			if (count <= 0)
				return [];

			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.MapLootEntries.Where(e => e.MapId == mapId)
				.Join(ctx.Items, e => e.ItemTemplateId, i => i.Id, (e, i) => i)
				.Where(i => i.Hash != 0 && i.Name != null && i.Name != "" && i.Type == ItemType.Normal && !i.ExcludeFromRandomLoot)
				.OrderBy(_ => EF.Functions.Random())
				.Take(count)
				.ToArrayAsync();
		}

		public async Task<Map[]> GetMaps()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Maps.OrderBy(m => m.Id).ToArrayAsync();
		}

		public async Task<QuestArea[]> GetQuestAreas()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.QuestAreas.OrderBy(a => a.AreaId).ToArrayAsync();
		}

		public async Task<QuestDefinition?> GetQuestDefinition(uint questId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.QuestDefinitions.Include(q => q.TextRefs).Include(q => q.Goals).FirstOrDefaultAsync(q => q.QuestId == questId);
		}

		// Every quest of an area, ordered by its position within that area (JournalSequence) - the quest index's
		// per-area id list.
		public async Task<QuestDefinition[]> GetQuestDefinitionsByArea(uint areaId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.QuestDefinitions.Where(q => q.AreaId == areaId).OrderBy(q => q.JournalSequence).ToArrayAsync();
		}

		public async Task<RespawnPoint?> GetRespawnPoint(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.RespawnPoints.FirstOrDefaultAsync(p => p.Id == id);
		}

		public async Task<RespawnPoint[]> GetRespawnPoints(uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.RespawnPoints.Where(r => r.MapId == mapId).ToArrayAsync();
		}

		public async Task<WorldObject?> GetWorldObject(uint mapId, uint captureId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.WorldObjects.FirstOrDefaultAsync(o => o.MapId == mapId && o.DisplayName != null && o.CapturedHandle == captureId);
		}

		public async Task<WorldObject[]> GetWorldObjects(uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.WorldObjects.Where(o => o.MapId == mapId && o.DisplayName != null).ToArrayAsync();
		}
	}
}
