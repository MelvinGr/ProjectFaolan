using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Repositories
{
	public interface INpcRepository
	{
		Task<int> DeleteNpcSpawns(uint mapId, string name, float? x, float? y, float? z, float radius);
		Task<ConversationNode?> GetConversationScreen(string npcName, uint typeId, string answerPath, uint clickedTemplateId);
		Task<ConversationNode[]> GetConversationScreens(string npcName, uint typeId, string answerPath, uint clickedTemplateId);
		Task<Dictionary<uint, uint>> GetEscortHiddenNpcMap();

		// The NPCs whose SERVER-OPENED conversation is triggered by using world object `worldObjectId` (#523).
		Task<string[]> GetServerInitiatedConversationNpcs(uint worldObjectId);

		// The OPENING screen ("" path) of the server-opened conversation `worldObjectId` triggers for `npcName` (#523).
		Task<ConversationNode[]> GetTriggeredConversations(string npcName, uint worldObjectId);

		// The NPCs whose SERVER-OPENED conversation fires on APPROACH rather than on using an object (#523).
		Task<string[]> GetApproachConversationNpcs();

		// The opening screens of the approach-triggered conversation for `npcName` (#523).
		Task<ConversationNode[]> GetApproachConversations(string npcName);
		Task<ConanClass[]> GetImportedSpawnClasses(uint mapId);
		Task<NpcTemplate?> GetNpcByName(string name);
		Task<NpcTemplate?> GetNpcByTemplateId(uint templateId);
		Task<NpcEncounter?> GetNpcEncounter(uint mapId, uint lureTemplateId);
		Task<NpcEncounter[]> GetNpcEncounters(uint mapId);
		Task<NpcSpawnArea[]> GetNpcSpawnAreas(uint mapId);
		Task<NpcSpawn?> GetNpcSpawnByName(string name);
		Task<NpcSpawn[]> GetNpcSpawns(uint mapId, ConanClass? forClass);
		Task<NpcSpell[]> GetNpcSpellsByName(string name);
		Task<Dictionary<string, uint[]>> GetQuestGatedNpcMap();
		Task<Dictionary<string, uint>> GetQuestGiverMap();
		Task<NpcSpawnArea[]> GetResolvedSpawnAreas(uint mapId);
		Task<Shop?> GetShopByTemplate(uint npcTemplateId);
		Task<int> GraduateSpawnArea(uint areaId);
		Task<int> LinkNpcSpawnsToArea(uint areaId, uint templateId, float selectedX, float selectedZ);
		Task<NpcSpawn[]> SearchNpcSpawns(string nameSubstring, int limit = 50);
		Task<bool> SetNpcSpawnAreaCreature(uint areaId, uint? templateId);
		Task<int> UnlinkNpcSpawnsFromArea(uint areaId);
	}

	public partial class DatabaseRepository
	{
		public async Task<int> DeleteNpcSpawns(uint mapId, string name, float? x, float? y, float? z, float radius)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var lowered = (name ?? "").ToLowerInvariant();
			var query = ctx.NpcSpawns.Where(n => n.MapId == mapId && n.NpcTemplate != null && n.NpcTemplate.Name != null && n.NpcTemplate.Name.ToLower() == lowered);
			if (x is { } px && y is { } py && z is { } pz)
			{
				query = query.Where(n =>
					n.PositionX >= px - radius && n.PositionX <= px + radius && n.PositionY >= py - radius && n.PositionY <= py + radius && n.PositionZ >= pz - radius && n.PositionZ <= pz + radius);
			}

			return await query.ExecuteDeleteAsync();
		}

		public async Task<ConversationNode?> GetConversationScreen(string npcName, uint typeId, string answerPath, uint clickedTemplateId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.ConversationNodes.Include(n => n.Packets)
				.ThenInclude(p => p.Screens)
				.ThenInclude(s => s.Parameters)
				.FirstOrDefaultAsync(n => n.NpcName == npcName && n.TypeId == typeId && n.AnswerPath == answerPath && (n.NpcTemplateId == null || n.NpcTemplateId == clickedTemplateId));
		}

		// The distinct NPCs whose server-opened conversation this world object triggers (#523). Retail's city door is
		// the worked example: using it opens Casilda's farewell.
		//
		// It is also what ENDS her escort, with no extra data: the object that opens the escortee's farewell is the
		// object the escort was leading to. That keeps the escort's completion data-driven off the trigger we already
		// harvested rather than needing a hard-coded door or a new column on the goal.
		public async Task<string[]> GetServerInitiatedConversationNpcs(uint worldObjectId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.ConversationNodes.Where(n => n.ServerInitiated && n.TriggerWorldObjectId == worldObjectId)
				.Select(n => n.NpcName)
				.Distinct()
				.ToArrayAsync();
		}

		// The OPENING screen of the conversation this world object triggers for this NPC (#523).
		//
		// ⚠️ It selects on the TRIGGER, not through the ordinary name/type/path lookup, because an NPC can have SEVERAL
		// server-opened conversations and they all sit at AnswerPath "" - Casilda has two. The name+path lookup cannot
		// tell them apart and would serve whichever came back first; the trigger is exactly what distinguishes them,
		// which is why it is part of the node's identity.
		public async Task<ConversationNode[]> GetTriggeredConversations(string npcName, uint worldObjectId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.ConversationNodes.Include(n => n.Packets)
				.ThenInclude(p => p.Screens)
				.ThenInclude(s => s.Parameters)
				.Where(n => n.ServerInitiated && n.TriggerWorldObjectId == worldObjectId && n.NpcName == npcName && n.AnswerPath == "")
				.OrderByDescending(n => n.ConditionQuestId != null) // conditioned first; the unconditional one is the fallback
				.ToArrayAsync();
		}

		// The NPCs with an APPROACH-triggered server conversation: server-opened, but with no world object behind them
		// (#523). Saddur's beach confrontation is the worked example - walking up to him is what opens it. Small and
		// static, so the caller caches it and the per-move check stays a pure in-memory name test.
		public async Task<string[]> GetApproachConversationNpcs()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.ConversationNodes.Where(n => n.ServerInitiated && n.TriggerWorldObjectId == null && n.AnswerPath == "")
				.Select(n => n.NpcName)
				.Distinct()
				.ToArrayAsync();
		}

		public async Task<ConversationNode[]> GetApproachConversations(string npcName)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.ConversationNodes.Include(n => n.Packets)
				.ThenInclude(p => p.Screens)
				.ThenInclude(s => s.Parameters)
				.Where(n => n.ServerInitiated && n.TriggerWorldObjectId == null && n.NpcName == npcName && n.AnswerPath == "")
				.OrderByDescending(n => n.ConditionQuestId != null)
				.ToArrayAsync();
		}

		public async Task<ConversationNode[]> GetConversationScreens(string npcName, uint typeId, string answerPath, uint clickedTemplateId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var nodes = await ctx
				.ConversationNodes.Include(n => n.Packets)
				.ThenInclude(p => p.Screens)
				.ThenInclude(s => s.Parameters)
				.Where(n => n.NpcName == npcName && n.TypeId == typeId && n.AnswerPath == answerPath && (n.NpcTemplateId == null || n.NpcTemplateId == clickedTemplateId))
				.ToListAsync();

			return nodes.OrderBy(n => n.ConditionQuestId == null).ToArray(); // conditioned variants first, unconditional fallback last
		}

		// #262: escortee NPCs that DISAPPEAR once their escort quest is COMPLETED (the escort is over - they're not back
		// at the spawn). Keyed by the escortee's TEMPLATE id (the Escort goal's TrailingRefId) -> the escort quest id.
		// Data-driven from the Escort goals; generalises to any escort (e.g. Casilda 9411 -> 12407).
		public async Task<Dictionary<uint, uint>> GetEscortHiddenNpcMap()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var escorts = await ctx
				.QuestGoalDefinitions.Where(g => g.GoalType == QuestGoalType.Escort && g.TrailingRefId != 0)
				.Select(g => new
				{
					g.TrailingRefId,
					g.QuestDefinitionId
				})
				.Distinct()
				.ToListAsync();

			return escorts.GroupBy(x => x.TrailingRefId).ToDictionary(g => g.Key, g => g.First().QuestDefinitionId);
		}

		// The same include set as GetNpcByName, keyed by the TYPE id - what a runtime spawn from a type needs (#502:
		// the encounter boss has no NpcSpawn row, so she can only be looked up this way).
		public async Task<NpcTemplate?> GetNpcByTemplateId(uint templateId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.NpcTemplates.Include(t => t.Spells)
				.Include(t => t.StatCore)
				.Include(t => t.Equipment)
				.ThenInclude(e => e.Item)
				.Include(t => t.Morphs)
				.FirstOrDefaultAsync(t => t.TemplateId == templateId);
		}

		public async Task<NpcEncounter?> GetNpcEncounter(uint mapId, uint lureTemplateId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.NpcEncounters.FirstOrDefaultAsync(e => e.MapId == mapId && e.LureNpcTemplateId == lureTemplateId);
		}

		public async Task<NpcEncounter[]> GetNpcEncounters(uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.NpcEncounters.Where(e => e.MapId == mapId).ToArrayAsync();
		}

		public async Task<NpcTemplate?> GetNpcByName(string name)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var lowered = name.ToLowerInvariant();
			return await ctx
				.NpcTemplates.Include(t => t.Spells)
				.Include(t => t.StatCore)
				.Include(t => t.Equipment)
				.ThenInclude(e => e.Item)
				.Include(t => t.Morphs)
				.Where(t => t.Name.ToLower() == lowered && t.StatCore != null)
				.FirstOrDefaultAsync();
		}

		public async Task<NpcSpawnArea[]> GetNpcSpawnAreas(uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.NpcSpawnAreas.Include(a => a.NpcTemplate).Where(a => a.MapId == mapId).ToArrayAsync();
		}

		public async Task<NpcSpawn?> GetNpcSpawnByName(string name)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();

			var lowered = name.ToLowerInvariant();
			return await ctx
				.NpcSpawns.Include(n => n.NpcTemplate)
				.ThenInclude(t => t.Spells)
				.Include(n => n.NpcTemplate)
				.ThenInclude(t => t.StatCore)
				.Include(n => n.Morphs)
				.Include(n => n.Equipment)
				.ThenInclude(e => e.Item)
				.Where(n => n.NpcTemplate.Name.ToLower() == lowered && n.NpcTemplate.StatCore != null)
				.FirstOrDefaultAsync();
		}

		// The classes a class-specific map (Map.ClassSpecific) actually has an imported population for - i.e. which
		// captures of it we hold. Empty means no class's copy has been imported at all. This is what the armory door
		// checks before teleporting: entering a class-specific playfield we have no set for would drop the player into
		// an empty room, so the gap is reported in-game instead (the same rule as an un-imported vendor).
		public async Task<ConanClass[]> GetImportedSpawnClasses(uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.NpcSpawns.Where(n => n.MapId == mapId && n.ClassId != null)
				.Select(n => n.ClassId!.Value)
				.Distinct()
				.ToArrayAsync();
		}

		// `forClass` selects the population of a CLASS-SPECIFIC map (Map.ClassSpecific - the city armories): a spawn
		// stamped with another class is not part of that character's copy of the playfield, while class-agnostic spawns
		// (ClassId null - every ordinary map) always belong. Pass null for NO class filter at all: the whole map, every
		// class, which is what an inspection/tooling caller wants and what the world entry must never ask for.
		// Deliberately not defaulted - a caller populating a live zone has to say whose copy it is building.
		public async Task<NpcSpawn[]> GetNpcSpawns(uint mapId, ConanClass? forClass)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.NpcSpawns.Include(n => n.Morphs)
				.Include(n => n.Equipment)
				.ThenInclude(e => e.Item)
				.Include(n => n.NpcTemplate)
				.ThenInclude(t => t.Spells)
				.Include(n => n.NpcTemplate)
				.ThenInclude(t => t.Chatter)
				.Include(n => n.NpcTemplate)
				.ThenInclude(t => t.StatCore)
				.Include(n => n.PatrolNodes)
				.ThenInclude(pn => pn.Waypoints)
				// Words too - lazy loading is off, and PatrolNodeMapper throws on a short word set rather than
				// rebuilding a wrong record, so a missing Include would break every patrol (#410).
				.Include(n => n.PatrolNodes)
				.ThenInclude(pn => pn.Words)
				.Where(n => n.MapId == mapId && (forClass == null || n.ClassId == null || n.ClassId == forClass))
				.ToArrayAsync();
		}

		public async Task<NpcSpell[]> GetNpcSpellsByName(string name)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var lowered = name.ToLowerInvariant();
			return await ctx.NpcSpells.Where(s => s.NpcTemplate.Name.ToLower() == lowered).ToArrayAsync();
		}

		// NPC name -> the quest ids that reveal it (#261). ⚠️ Membership is the goal's own GatesTargetVisibility flag,
		// NOT "a Kill goal that happens to carry a TargetName". The gate keys on the NAME and forces NoRespawn, so
		// implying it from the target made every mechanically-resolved kill target (#521) hide its whole population and
		// stop respawning - see QuestGoalDefinition.GatesTargetVisibility for the quest that broke.
		public async Task<Dictionary<string, uint[]>> GetQuestGatedNpcMap()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var givers = (await ctx.ConversationNodes.Where(n => n.GrantQuestId > 0).Select(n => n.NpcName).Distinct().ToListAsync()).ToHashSet();

			var killTargets = await ctx
				.QuestGoalDefinitions.Where(g => g.GatesTargetVisibility && g.TargetName != null && g.TargetName != "")
				.Join(ctx.QuestDefinitions, g => g.QuestDefinitionId, d => d.QuestId, (g, d) => new
				{
					g.TargetName,
					d.QuestId
				})
				.Distinct()
				.ToListAsync();

			return killTargets.Where(x => !givers.Contains(x.TargetName!)).GroupBy(x => x.TargetName!).ToDictionary(g => g.Key, g => g.Select(x => x.QuestId).Distinct().ToArray());
		}

		public async Task<Dictionary<string, uint>> GetQuestGiverMap()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var grants = await ctx
				.ConversationNodes.Where(n => n.GrantQuestId > 0)
				.Select(n => new
				{
					n.NpcName,
					n.GrantQuestId
				})
				.Distinct()
				.ToListAsync();
			return grants.GroupBy(g => g.NpcName).ToDictionary(g => g.Key, g => g.Min(x => x.GrantQuestId!.Value));
		}

		public async Task<NpcSpawnArea[]> GetResolvedSpawnAreas(uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.NpcSpawnAreas.Include(a => a.NpcTemplate)
				.ThenInclude(t => t!.StatCore)
				.Include(a => a.NpcTemplate)
				.ThenInclude(t => t!.Spells)
				.Include(a => a.Points)
				.Where(a => a.MapId == mapId && a.NpcTemplateId != null)
				.ToArrayAsync();
		}

		public async Task<Shop?> GetShopByTemplate(uint npcTemplateId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Shops.Include(s => s.Items).FirstOrDefaultAsync(s => s.NpcTemplateId == npcTemplateId);
		}

		public async Task<int> GraduateSpawnArea(uint areaId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.NpcSpawns.Where(s => s.NpcSpawnAreaId == areaId).ExecuteDeleteAsync();
		}

		public async Task<int> LinkNpcSpawnsToArea(uint areaId, uint templateId, float selectedX, float selectedZ)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var area = await ctx.NpcSpawnAreas.FindAsync(areaId);
			if (area == null)
				return 0;

			const float pad = 5f;
			const float sel = 2f;
			var toLink = await ctx
				.NpcSpawns.Where(s =>
					s.MapId == area.MapId && s.NpcTemplateId == templateId &&
					((s.PositionX >= area.BoundsMinX - pad && s.PositionX <= area.BoundsMaxX + pad && s.PositionZ >= area.BoundsMinZ - pad && s.PositionZ <= area.BoundsMaxZ + pad) ||
					 (s.PositionX >= selectedX - sel && s.PositionX <= selectedX + sel && s.PositionZ >= selectedZ - sel && s.PositionZ <= selectedZ + sel)))
				.ToListAsync();

			foreach (var s in toLink)
				s.NpcSpawnAreaId = areaId;
			await ctx.SaveChangesAsync();
			return toLink.Count;
		}

		public async Task<NpcSpawn[]> SearchNpcSpawns(string nameSubstring, int limit = 50)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var lowered = nameSubstring.ToLowerInvariant();
			return await ctx
				.NpcSpawns.Include(n => n.NpcTemplate)
				.ThenInclude(t => t.StatCore)
				.Where(n => n.NpcTemplate.Name.ToLower().Contains(lowered))
				.OrderBy(n => n.NpcTemplate.Name)
				.ThenBy(n => n.MapId)
				.Take(limit)
				.ToArrayAsync();
		}

		public async Task<bool> SetNpcSpawnAreaCreature(uint areaId, uint? templateId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.NpcSpawnAreas.Where(a => a.Id == areaId)
				.ExecuteUpdateAsync(s => s
					.SetProperty(sp => sp.NpcTemplateId, templateId)
					.SetProperty(sp => sp.ResolvedBy, templateId.HasValue
						? SpawnResolutionSource.Manual
						: SpawnResolutionSource.None)) == 1;
		}

		public async Task<int> UnlinkNpcSpawnsFromArea(uint areaId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.NpcSpawns.Where(s => s.NpcSpawnAreaId == areaId).ExecuteUpdateAsync(s => s.SetProperty(sp => sp.NpcSpawnAreaId, (uint?)null));
		}
	}
}
