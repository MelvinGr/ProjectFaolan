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
		Task<MapRegion[]> GetNoMountRegions(uint mapId);
		Task<TravelDestination[]> GetTravelDestinationsFrom(uint fromNodeId);
		Task<TravelDestination?> GetTravelDestination(uint fromNodeId, uint optionId);
		Task<TravelDestination[]> GetTravelDestinationsByNpcName(string npcName);
		Task<Item[]> GetMapLootSample(uint mapId, int count);
		Task<Map[]> GetMaps();
		Task<QuestLine[]> GetQuestLines();
		Task<QuestDefinition?> GetQuestDefinition(uint questId);

		// The pick-one-of-N reward list `questId`'s completion offers - harvested capture rows first, the quest line's
		// optional set as a fallback on the line's final quest. Empty when the quest offers no choice.
		Task<QuestRewardOption[]> GetQuestRewardOptions(uint questId);
		Task<QuestRewardOption[]> GetQuestGuaranteedRewards(uint questId);
		Task<Dictionary<uint, Item>> GetItemsByTemplate(IReadOnlyCollection<uint> templateIds);
		Task<QuestDefinition[]> GetQuestDefinitions(IReadOnlyCollection<uint> questIds);

		/// <summary>
		///     Which LINE each of <paramref name="questIds" /> sits on, at what level and step - the three words the
		///     quest-marker rules read off a definition, and nothing else (#939).
		/// </summary>
		/// <remarks>
		///     A projection, not an entity load: the marker resolver used to fetch every held quest's full definition
		///     through <see cref="GetQuestDefinition" /> - the five-level Include - ONE QUERY EACH, once per NPC it was
		///     asked about, to read these three columns. For a level-80 that was ~1,565 graph loads per NPC.
		/// </remarks>
		Task<Dictionary<uint, QuestLineMembership>> GetQuestLineMembership(IReadOnlyCollection<uint> questIds);

		/// <summary>
		///     The completed-journal read (#938): the definitions for <paramref name="questIds" /> with ONLY their
		///     <c>TextRefs</c>, and only the lines those quests sit on, with no navigations at all.
		/// </summary>
		/// <remarks>
		///     ⭐ Shaped by what the completed tab RENDERS - a title and a sub-line per quest, grouped under a line's
		///     three names - and nothing else. <see cref="GetQuestDefinitions" /> is the ACTIVE journal's read and
		///     carries the whole graph (goals, their regions and vertices, reward options, the line's rewards)
		///     because the active journal needs it; using it for the completed tab was a five-level Include over
		///     every quest the character had ever finished, on every login: 2.6 s for a level-80 with 1,565
		///     completions, against 79 ms for a level-4.
		/// </remarks>
		Task<(QuestDefinition[] Definitions, QuestLine[] Lines)> GetCompletedJournal(IReadOnlyCollection<uint> questIds);
		Task<(CharacterQuest Quest, QuestDefinition Definition)[]> GetCharacterQuestsWithDefinitions(uint characterId, QuestState? state = null);

		/// <summary>
		///     The template the character is (or was) escorting: the <c>TrailingRefId</c> of the first Escort goal on
		///     any Active or Completed quest they hold, or 0 (#940).
		/// </summary>
		/// <remarks>
		///     One projection over the goal rows. <c>TortageIntroService</c> used to answer this by loading EVERY quest
		///     the character had ever held with the five-level Include and walking their goals - 4.3-4.9 s of every
		///     world entry for a level-80, paid to ask whether Casilda is still captive. "First" is the held order the
		///     walk had: lowest quest id, then lowest goal id.
		/// </remarks>
		Task<uint> GetEscorteeTemplateId(uint characterId);
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

		// The volumes a playfield forbids riding in (#681). Boundary polygon included for the same reason as every
		// other consumer: containment is a point-in-polygon walk, and lazy loading is off.
		public async Task<MapRegion[]> GetNoMountRegions(uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.MapRegions.Where(r => r.MapId == mapId && r.NoMount)
				.Include(r => r.Vertices)
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
				// No `Hash != 0` here - every item has a wire token since #723 (harvested or minted), so that filter
				// would only drop the templates our harvest has not reached. A name is still required.
				.Where(i => !string.IsNullOrEmpty(i.Name) && i.Type == ItemType.Normal && !i.ExcludeFromRandomLoot)
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
			return await ctx.QuestLines.Include(l => l.Rewards).OrderBy(a => a.QuestLineId).ToArrayAsync();
		}

		// The pick-one-of-N reward list a quest's completion offers (#815), resolved CAPTURE-FIRST.
		//
		// Two sources, and they are not equals. A harvested `QuestRewardOptions` row is retail's own offer, read off the
		// wire; the client's quest catalog only ever states rewards on the LINE, and for the single offer we have
		// captured the two DISAGREE - quest 10928 offered three Acolyte weapons while its line 386 holds one
		// "Exponent's Gloves" and those three ids appear on no line at all. Where a capture and the client's data
		// disagree the capture wins, so the harvested set is taken whole and the catalog is not consulted.
		//
		// The catalog fallback covers the 615 lines that carry a genuine multi-item optional set and that no capture
		// has ever watched complete. It fires only on the line's FINAL quest - the client's own test, the last entry of
		// the line's list - because a line reward is the payoff for finishing the line.
		// ⭐ That timing was written as a decision and is an observation now (#893): 574 captured last-step journal
		// records carry their line's catalog lists as the quest's own obj1/obj2, and every mid step carries them as the
		// obj3/obj4 preview instead. The disagreement quoted above (10928 vs its line) is the two halves of that - the
		// quest's own set and the line's - not a capture contradicting the catalog.
		public async Task<QuestRewardOption[]> GetQuestRewardOptions(uint questId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();

			var harvested = await ctx.QuestRewardOptions.AsNoTracking()
				.Where(o => o.QuestId == questId)
				.OrderBy(o => o.Pos)
				.ToArrayAsync();
			if (harvested.Length > 0)
				return harvested;

			var quest = await ctx.QuestDefinitions.AsNoTracking().FirstOrDefaultAsync(q => q.QuestId == questId);
			if (quest == null || quest.NextQuestId != null)
				return [];

			// Projected onto the same row type so the caller has one shape to build from. Pos is re-numbered densely
			// from the catalog's own order: the client answers with an INDEX into the list we send, which is not the
			// catalog's `Pos` (that numbers fixed and optional rewards in one sequence).
			var line = await ctx.QuestLineRewards.AsNoTracking()
				.Where(r => r.QuestLineId == quest.QuestLineId && r.Optional)
				.OrderBy(r => r.Pos)
				.Select(r => r.ItemTemplateId)
				.ToArrayAsync();

			return line.Distinct()
				.Select((template, i) => new QuestRewardOption
				{
					QuestId = questId,
					Pos = (uint)i,
					ItemTemplateId = template,
					Level = 0 // the catalog states no level; the offer falls back to the item's own
				})
				.ToArray();
		}

		// The items a quest's completion GRANTS outright - the record's obj1 (#893): the harvested own set's
		// non-optional rows, or on the line's last step with nothing harvested, the line's catalog `reward` list.
		public async Task<QuestRewardOption[]> GetQuestGuaranteedRewards(uint questId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();

			var harvested = await ctx.QuestRewardOptions.AsNoTracking()
				.Where(o => o.QuestId == questId)
				.OrderBy(o => o.Pos)
				.ToArrayAsync();
			if (harvested.Length > 0)
				return harvested.Where(o => !o.Optional).ToArray();

			var quest = await ctx.QuestDefinitions.AsNoTracking().Include(q => q.Line).FirstOrDefaultAsync(q => q.QuestId == questId);
			if (quest == null || quest.Line.LastQuestId != questId)
				return [];

			return (await ctx.QuestLineRewards.AsNoTracking()
					.Where(r => r.QuestLineId == quest.QuestLineId && !r.Optional)
					.OrderBy(r => r.Pos)
					.ToArrayAsync())
				.Select((r, i) => new QuestRewardOption
				{
					QuestId = questId,
					Pos = (uint)i,
					ItemTemplateId = r.ItemTemplateId,
					Level = 0,
					Optional = false,
					Quantity = r.Quantity ?? 1
				})
				.ToArray();
		}

		// Item rows by template id, for the reward-list builder's key/level lookup. Templates we do not hold are
		// simply absent from the result - the builder leaves those elements out rather than sending a zero key.
		public async Task<Dictionary<uint, Item>> GetItemsByTemplate(IReadOnlyCollection<uint> templateIds)
		{
			if (templateIds.Count == 0)
				return [];

			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var ids = templateIds.Distinct().ToArray();
			return await ctx.Items.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
		}

		public async Task<QuestDefinition?> GetQuestDefinition(uint questId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			// Goals bring their ReachArea region + ring (#521); same reason as GetRespawnPoints - lazy loading is off,
			// so an un-Included navigation is silently null/empty rather than loaded on touch.
			// Rewards (this quest's own set) and Line.Rewards (the line's, from the catalog) feed the journal record's
			// four reward lists (#893); the Item rows behind them come through GetItems, keyed by template.
			return await ctx
				.QuestDefinitions.Include(q => q.TextRefs)
				.Include(q => q.Goals)
				.ThenInclude(g => g.Region!.Vertices)
				.Include(q => q.Rewards)
				.Include(q => q.Line.Rewards)
				.AsSplitQuery()
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
		public async Task<(QuestDefinition[] Definitions, QuestLine[] Lines)> GetCompletedJournal(IReadOnlyCollection<uint> questIds)
		{
			if (questIds.Count == 0)
				return ([], []);

			var definitions = new List<QuestDefinition>(questIds.Count);
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			foreach (var chunk in questIds.Distinct().Chunk(500))
			{
				definitions.AddRange(await ctx
					.QuestDefinitions.AsNoTracking()
					.Include(q => q.TextRefs)
					.Where(q => chunk.Contains(q.QuestId))
					.ToArrayAsync());
			}

			// Only the lines these quests sit on, and no navigations: the tab writes a line's names and two words,
			// never its rewards.
			var lineIds = definitions.Select(d => d.QuestLineId).Where(id => id != 0).Distinct().ToArray();
			var lines = new List<QuestLine>(lineIds.Length);
			foreach (var chunk in lineIds.Chunk(500))
			{
				lines.AddRange(await ctx
					.QuestLines.AsNoTracking()
					.Where(l => chunk.Contains(l.QuestLineId))
					.ToArrayAsync());
			}

			return ([.. definitions], [.. lines]);
		}

		public async Task<Dictionary<uint, QuestLineMembership>> GetQuestLineMembership(IReadOnlyCollection<uint> questIds)
		{
			var result = new Dictionary<uint, QuestLineMembership>(questIds.Count);
			if (questIds.Count == 0)
				return result;

			await using var ctx = await _contextFactory.CreateDbContextAsync();
			foreach (var chunk in questIds.Distinct().Chunk(500))
			{
				var rows = await ctx.QuestDefinitions.AsNoTracking()
					.Where(q => chunk.Contains(q.QuestId))
					.Select(q => new QuestLineMembership(q.QuestId, q.QuestLineId, q.Level, q.JournalSequence))
					.ToArrayAsync();
				foreach (var row in rows)
					result[row.QuestId] = row;
			}

			return result;
		}

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
					.Include(q => q.Rewards)
					.Include(q => q.Line.Rewards)
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
		public async Task<uint> GetEscorteeTemplateId(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterQuests.AsNoTracking()
				.Where(q => q.CharacterId == characterId && (q.State == QuestState.Active || q.State == QuestState.Completed))
				.Join(ctx.QuestGoalDefinitions.AsNoTracking().Where(g => g.GoalType == QuestGoalType.Escort && g.TrailingRefId != 0),
					q => q.QuestId, g => g.QuestDefinitionId, (q, g) => new { q.QuestId, g.GoalId, g.TrailingRefId })
				.OrderBy(x => x.QuestId).ThenBy(x => x.GoalId)
				.Select(x => x.TrailingRefId)
				.FirstOrDefaultAsync();
		}

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

	// The quest-marker rules' view of a definition (#939): its line, its level (the silver band), its step (the
	// line-reached gate). See IWorldRepository.GetQuestLineMembership.
	public sealed record QuestLineMembership(uint QuestId, uint QuestLineId, uint Level, uint JournalSequence);
}
