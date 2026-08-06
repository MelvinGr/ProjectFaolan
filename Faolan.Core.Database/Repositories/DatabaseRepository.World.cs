using System.Collections.Generic;
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
		Task<MapRegion[]> GetAreaBannerRegions(uint mapId);
		Task<MapHazard[]> GetMapHazards(uint mapId);
		Task<TravelDestination[]> GetTravelDestinationsFrom(uint fromNodeId);
		Task<TravelDestination?> GetTravelDestination(uint fromNodeId, uint optionId);
		Task<TravelDestination[]> GetTravelDestinationsByNpcName(string npcName);
		Task<Item[]> GetMapLootSample(uint mapId, int count);
		Task<Map[]> GetMaps();
		Task<QuestLine[]> GetQuestLines();
		Task<QuestDefinition?> GetQuestDefinition(uint questId);
		Task<QuestDefinition[]> GetQuestDefinitions(IReadOnlyCollection<uint> questIds);
		Task<(CharacterQuest Quest, QuestDefinition Definition)[]> GetCharacterQuestsWithDefinitions(uint characterId, QuestState? state = null);
		Task<QuestDefinition[]> GetQuestDefinitionsByLine(uint questLineId);
		Task<Dictionary<uint, QuestDefinition[]>> GetQuestDefinitionsByLines(IReadOnlyCollection<uint> questLineIds);
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

		// The named sub-areas of a playfield - the client's own trigger volumes that float the area banner when a
		// player walks in (#348/#513/#679). Every one carries its boundary polygon: containment is a point-in-polygon
		// walk, so a volume without vertices would silently never fire. (Lazy loading is deliberately off, so the
		// Include is required - see CLAUDE.md.)
		public async Task<MapRegion[]> GetAreaBannerRegions(uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.MapRegions.Where(r => r.MapId == mapId && r.AreaNameId != null)
				.Include(r => r.Vertices)
				.ToArrayAsync();
		}

		// The destinations offered from one dialogue SCREEN - the list screen, which is where retail charges the fare
		// (#514). Empty for every other screen in the game, which is the common case and not an error.
		//
		// #646: this used to fetch by NPC NAME and then filter the result down to this screen, which asked the database
		// a question ("what does this name offer?") that the caller then narrowed to the one it actually had - and it
		// asked it with the ambiguous half of the NPC's identity. The screen IS half of the table's primary key, so
		// keying on it is both narrower and exact.
		public async Task<TravelDestination[]> GetTravelDestinationsFrom(uint fromNodeId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.TravelDestinations.Where(d => d.FromNodeId == fromNodeId).ToArrayAsync();
		}

		// The trip a single dialogue PICK buys, by the table's full primary key. Null when that branch sells no trip -
		// true of every other conversation branch, so it is the normal answer rather than an error.
		public async Task<TravelDestination?> GetTravelDestination(uint fromNodeId, uint optionId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.TravelDestinations.FirstOrDefaultAsync(d => d.FromNodeId == fromNodeId && d.OptionId == optionId);
		}

		// By NPC display NAME - for the `.travel` GM bootstrap ONLY, because a GM types a name. Nothing on the retail
		// path resolves a traveller this way any more (#646).
		public async Task<TravelDestination[]> GetTravelDestinationsByNpcName(string npcName)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.TravelDestinations.Where(d => d.NpcName == npcName).ToArrayAsync();
		}

		// The scripted hazard areas of a playfield - the regions that hurt or kill on entry (#504). A hazard is the
		// BEHAVIOUR half, so its region comes with it, boundary polygon and all: containment is a point-in-polygon
		// test, and a hazard without vertices is not a hazard. (Lazy loading is deliberately off, so the Includes are
		// required - see CLAUDE.md.)
		public async Task<MapHazard[]> GetMapHazards(uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.MapHazards.Where(h => h.Region.MapId == mapId)
				.Include(h => h.Region)
				.ThenInclude(r => r.Vertices)
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

		// The batched form of GetQuestDefinition, for the login path. The snapshot needs a definition for EVERY quest in
		// the character's journal, and doing that one id at a time meant one round trip - and one fresh DbContext - per
		// quest: 158 of them for a level-23 character (~3.0s) and 1512 for a level-80 one (~25.3s), on a path retail
		// completes in 0.8s. Same Includes as the singular form, so callers get identical rows.
		//
		// Split query for the same reason it is on GetNpcSpawns: TextRefs, Goals and each Goal's region ring are three
		// collection levels, and one JOIN multiplies them together. Chunked so a journal of any size stays clear of a
		// provider's IN-list ceiling - the round trips are then per 500 quests, not per quest.
		public async Task<QuestDefinition[]> GetQuestDefinitions(IReadOnlyCollection<uint> questIds)
		{
			if (questIds.Count == 0)
				return [];

			var definitions = new List<QuestDefinition>(questIds.Count);
			foreach (var chunk in questIds.Distinct().Chunk(500))
			{
				await using var ctx = await _contextFactory.CreateDbContextAsync();
				definitions.AddRange(await ctx
					.QuestDefinitions.Include(q => q.TextRefs)
					.Include(q => q.Goals)
					.ThenInclude(g => g.Region!.Vertices)
					.Where(q => chunk.Contains(q.QuestId))
					.AsSplitQuery()
					.ToArrayAsync());
			}

			return [.. definitions];
		}

		// The character's held quests in the given STATE, each paired with its definition, in CharacterQuests order -
		// as ONE batched read. Null (the default) takes every held quest.
		//
		// ⚠️ This is THE way to ask "the player's quests and what they require". Every goal-matching path needs the
		// pair, and each used to walk the quests calling GetQuestDefinition per quest: a query per quest, each on its
		// own DbContext. Those paths are live ones - a kill, a pickup, a door use, a loot, the 0x2020 spawn poll - so
		// the cost was paid over and over during play, and the escort lookup walked COMPLETED quests too (1500+ on a
		// level-80 character). Batching it at the call site worked but had to be re-derived every time; here it is
		// impossible to get wrong.
		//
		// A quest whose definition is missing is DROPPED, which is what every caller already did - the ones reading
		// `def?.Goals` fell through to `goal == null -> continue`, the rest tested `def == null` explicitly.
		public async Task<(CharacterQuest Quest, QuestDefinition Definition)[]> GetCharacterQuestsWithDefinitions(uint characterId, QuestState? state = null)
		{
			var held = await GetCharacterQuests(characterId, state);
			if (held.Length == 0)
				return [];

			var definitions = (await GetQuestDefinitions([.. held.Select(q => q.QuestId)])).ToDictionary(d => d.QuestId);

			return [.. held.Where(q => definitions.ContainsKey(q.QuestId)).Select(q => (q, definitions[q.QuestId]))];
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

		// The batched form, for the login quest INDEX: it needs the catalog of every line the character holds a quest in,
		// and asking line by line was a query each. Same filter and same JournalSequence order as the singular form, so a
		// line's list is identical either way - a line with no qualifying step is simply absent, exactly as an empty
		// array from the singular form would be.
		public async Task<Dictionary<uint, QuestDefinition[]>> GetQuestDefinitionsByLines(IReadOnlyCollection<uint> questLineIds)
		{
			if (questLineIds.Count == 0)
				return [];

			var ids = questLineIds.Distinct().ToArray();
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var rows = await ctx
				.QuestDefinitions.Where(q => ids.Contains(q.QuestLineId) && q.TextRefs.Any())
				.OrderBy(q => q.JournalSequence)
				.ToArrayAsync();

			return rows.GroupBy(q => q.QuestLineId).ToDictionary(g => g.Key, g => g.ToArray());
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
