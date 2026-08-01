using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Repositories
{
	public interface IWorldRepository
	{
		Task<int> DeleteWorldObject(uint id);
		Task<RespawnPoint?> GetDefaultSpawn(uint mapId);
		Task<Map?> GetMap(uint id);
		Task<MapArea[]> GetMapAreas(uint mapId);
		Task<MapHazard[]> GetMapHazards(uint mapId);
		Task<TravelDestination[]> GetTravelDestinations(string npcName);
		Task<Item[]> GetMapLootSample(uint mapId, int count);
		Task<Map[]> GetMaps();
		Task<QuestLine[]> GetQuestLines();
		Task<QuestDefinition?> GetQuestDefinition(uint questId);
		Task<QuestDefinition[]> GetQuestDefinitionsByLine(uint questLineId);
		Task<RespawnPoint?> GetRespawnPoint(uint id);
		Task<RespawnPoint[]> GetRespawnPoints(uint mapId);
		Task<WorldObject[]> GetWorldObjects(uint mapId);
	}

	public partial class DatabaseRepository
	{
		// By the row's own derived id - the stable key. It used to take (mapId, capturedHandle), which meant a GM
		// .delete resolved a LIVE wire handle straight against a column (see LiveWorldObject).
		public async Task<int> DeleteWorldObject(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.WorldObjects.Where(o => o.Id == id).ExecuteDeleteAsync();
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

		// The destinations a local-travel NPC offers, keyed by its NAME (#506) - the stable key, since a spawn handle is
		// session-volatile. Empty for an NPC that is not a traveller, which is the common case and not an error.
		public async Task<TravelDestination[]> GetTravelDestinations(string npcName)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.TravelDestinations.Where(d => d.NpcName == npcName).ToArrayAsync();
		}

		// The scripted hazard areas of a playfield - the regions that hurt or kill on entry (#504). Includes the
		// boundary polygon: containment is a point-in-polygon test, so a hazard without its vertices is not a hazard.
		// (Lazy loading is deliberately off, so the Include is required - see CLAUDE.md.)
		public async Task<MapHazard[]> GetMapHazards(uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.MapHazards.Where(h => h.MapId == mapId)
				.Include(h => h.Vertices)
				.ToArrayAsync();
		}

		public async Task<Item[]> GetMapLootSample(uint mapId, int count)
		{
			if (count <= 0)
				return [];

			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.MapLootEntries.Where(e => e.MapId == mapId)
				.Join(ctx.Items, e => e.ItemTemplateId, i => i.Id, (e, i) => i)
				.Where(i => i.Hash != 0 && !string.IsNullOrEmpty(i.Name) && i.Type == ItemType.Normal && !i.ExcludeFromRandomLoot)
				.OrderBy(_ => EF.Functions.Random())
				.Take(count)
				.ToArrayAsync();
		}

		public async Task<Map[]> GetMaps()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Maps.OrderBy(m => m.Id).ToArrayAsync();
		}

		public async Task<QuestLine[]> GetQuestLines()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.QuestLines.OrderBy(a => a.QuestLineId).ToArrayAsync();
		}

		public async Task<QuestDefinition?> GetQuestDefinition(uint questId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			// Goals bring their ReachArea region + ring (#521); same reason as GetRespawnPoints - lazy loading is off,
			// so an un-Included navigation is silently null/empty rather than loaded on touch.
			return await ctx
				.QuestDefinitions.Include(q => q.TextRefs)
				.Include(q => q.Goals)
				.ThenInclude(g => g.Region!.Vertices)
				.FirstOrDefaultAsync(q => q.QuestId == questId);
		}

		// Every step of a quest line, ordered by its position within that line (JournalSequence) - the quest index's
		// per-line id list, which QuestIndexRegions.ForQuests turns into a block's `AreaQuestIds` and its
		// (sequence, remaining) pair.
		//
		// ⚠️ Restricted to steps that have journal TEXT REFS, and that filter is load-bearing on the login path. The
		// client's quest catalog (RDB 1000104) contributed 3011 quests no capture ever showed, and 56 of the 82
		// snapshot-eligible quests share a line with one; without this filter their index block would list ids the
		// captures never carried and a `remaining` count to match, which is a region we cannot prove against any
		// capture going out on a live login. Having text refs is the right test rather than a provenance flag: this
		// list IS what the journal renders, and a quest with no journal text cannot be rendered in it. It splits the
		// two sources exactly - all 2037 captured quests have refs, all 3011 catalog-only ones have none.
		//
		// ⚠️ Do NOT filter on JournalSequence != 0 instead. 0 is a legitimate captured value (74 captured quests carry
		// it; 182 lines list every quest with 0), so that would drop real steps.
		public async Task<QuestDefinition[]> GetQuestDefinitionsByLine(uint questLineId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.QuestDefinitions.Where(q => q.QuestLineId == questLineId && q.TextRefs.Any())
				.OrderBy(q => q.JournalSequence)
				.ToArrayAsync();
		}

		public async Task<RespawnPoint?> GetRespawnPoint(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.RespawnPoints.FirstOrDefaultAsync(p => p.Id == id);
		}

		public async Task<RespawnPoint[]> GetRespawnPoints(uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			// The discovery volume + its ring come along: WorldSimulation tests containment per player per second, and
			// a lazy load there would be a query per point per tick (lazy loading is off anyway, so it would silently
			// be an EMPTY ring instead - a region that can never contain anyone).
			return await ctx
				.RespawnPoints.Include(r => r.DiscoveryRegion)
				.ThenInclude(g => g!.Vertices)
				.Where(r => r.MapId == mapId)
				.ToArrayAsync();
		}

		// Include the WORD rows: they are not decoration, they are part of the wire record. WorldObjectGeometryCodec
		// writes the geometry-node words and then the per-class trailer (1 u32 for Door and ClimbingDynel, 5 for
		// Destructible, none for SimpleDynel) straight out of this navigation, and the repo does not lazy-load - so
		// without the Include those objects go out SHORT and the client's ReadSubClass fails on them and drops them:
		// "ReadSubClass for IIR 866480048 (DoorFullUpdateIIR_c) failed ... datasize left in stream=0". Doors, climbing
		// vines and destructibles simply never appeared, while SimpleDynel props - which own no words - looked fine.
		public async Task<WorldObject[]> GetWorldObjects(uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.WorldObjects.Include(o => o.Words).Where(o => o.MapId == mapId && o.DisplayKind != null).ToArrayAsync();
		}
	}
}
