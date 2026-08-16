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

		// The ROOTS of the NPC TEMPLATE's dialogue for one trigger (#642/#646), each with its opening screen loaded:
		// talk roots (`worldObjectId` null), the use-object root of that object, or the approach root. Several can
		// share a trigger and differ only by quest gate, so the caller picks (ConversationService.PickByLatestQuest).
		Task<ConversationEntry[]> GetConversationEntries(uint npcTemplateId, ConversationTrigger trigger, uint? worldObjectId);

		// The branches OUT of one screen, each with its destination screen loaded (#642).
		Task<ConversationEdge[]> GetConversationEdges(uint fromNodeId);
		Task<Dictionary<uint, uint>> GetEscortHiddenNpcMap();

		// The NPC TEMPLATES whose SERVER-OPENED conversation is triggered by using world object `worldObjectId` (#523).
		Task<uint[]> GetServerInitiatedConversationNpcs(uint worldObjectId);

		// The NPC TEMPLATES whose SERVER-OPENED conversation fires on APPROACH rather than on using an object (#523).
		Task<uint[]> GetApproachConversationNpcs();
		Task<ConanClass[]> GetImportedSpawnClasses(uint mapId);
		Task<NpcTemplate?> GetNpcByName(string name);
		Task<NpcTemplate?> GetNpcByTemplateId(uint templateId);
		Task<NpcEncounter?> GetNpcEncounter(uint mapId, uint lureTemplateId);
		Task<NpcEncounter[]> GetNpcEncounters(uint mapId);

		// #528: the quest-triggered ambushes on `mapId` - the NPCs that appear only when a goal advances. Read once
		// per zone load, both to arm the trigger and to SKIP the captured placements of those types.
		Task<NpcAmbush[]> GetNpcAmbushes(uint mapId);

		// The ambush a goal springs, or null. Keyed by the goal because that is what the progression engine holds.
		Task<NpcAmbush?> GetNpcAmbushForGoal(uint questDefinitionId, uint goalId);
		Task<NpcSpawnArea[]> GetNpcSpawnAreas(uint mapId);
		Task<NpcSpawn?> GetNpcSpawnByName(string name);
		Task<NpcSpawn[]> GetNpcSpawns(uint mapId, ConanClass? forClass);
		Task<NpcSpell[]> GetNpcSpellsByName(string name);
		Task<Dictionary<uint, uint[]>> GetQuestGatedNpcMap();
		Task<HashSet<uint>> GetStoryKillTargets();
		Task<Dictionary<uint, uint>> GetQuestGiverMap();
		// The NPC TEMPLATES that play `profession` - the client's own role tag (#647), not a display-name match.
		Task<uint[]> GetNpcTemplatesByProfession(NpcProfession profession);
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

		// The ROOTS of an NPC's dialogue for one trigger, each with its opening screen fully loaded (#642).
		//
		// One lookup replaces the four that used to differ only in which of `ServerInitiated` / `TriggerWorldObjectId` /
		// `AnswerPath == ""` they filtered on. The trigger is now stated by a column, so "which of Casilda's two
		// server-opened conversations is this" is answered by the key rather than by whichever row came back first.
		// #646: keyed by the NPC's TEMPLATE, which is the identity, rather than by its display name with the template as
		// a nullable side-gate. The gate is gone because the key does its job.
		public async Task<ConversationEntry[]> GetConversationEntries(uint npcTemplateId, ConversationTrigger trigger, uint? worldObjectId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.ConversationEntries.Include(e => e.Node)
				.ThenInclude(n => n.Packets)
				.ThenInclude(p => p.Screens)
				.ThenInclude(s => s.Parameters)
				.Where(e => e.NpcTemplateId == npcTemplateId && e.Trigger == trigger && e.TriggerWorldObjectId == worldObjectId)
				.ToArrayAsync();
		}

		// The branches out of one screen, each with its destination screen fully loaded (#642) - one round-trip per
		// dialogue pick, the same as the old path lookup cost.
		public async Task<ConversationEdge[]> GetConversationEdges(uint fromNodeId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.ConversationEdges.Include(e => e.To)
				.ThenInclude(n => n.Packets)
				.ThenInclude(p => p.Screens)
				.ThenInclude(s => s.Parameters)
				.Where(e => e.FromNodeId == fromNodeId)
				.ToArrayAsync();
		}

		// The distinct NPCs whose server-opened conversation this world object triggers (#523). Retail's city door is
		// the worked example: using it opens Casilda's farewell.
		//
		// It is also what ENDS her escort, with no extra data: the object that opens the escortee's farewell is the
		// object the escort was leading to. That keeps the escort's completion data-driven off the trigger we already
		// harvested rather than needing a hard-coded door or a new column on the goal.
		public async Task<uint[]> GetServerInitiatedConversationNpcs(uint worldObjectId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.ConversationEntries.Where(e => e.Trigger == ConversationTrigger.UseWorldObject && e.TriggerWorldObjectId == worldObjectId)
				.Select(e => e.NpcTemplateId)
				.Distinct()
				.ToArrayAsync();
		}

		// The NPCs with an APPROACH-triggered server conversation (#523). Saddur's beach confrontation is the worked
		// example - walking up to him is what opens it. Small and static, so the caller caches it and the per-move
		// check stays a pure in-memory name test.
		public async Task<uint[]> GetApproachConversationNpcs()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.ConversationEntries.Where(e => e.Trigger == ConversationTrigger.Approach)
				.Select(e => e.NpcTemplateId)
				.Distinct()
				.ToArrayAsync();
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

		public async Task<NpcAmbush[]> GetNpcAmbushes(uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.NpcAmbushes.Where(a => a.MapId == mapId).ToArrayAsync();
		}

		public async Task<NpcAmbush?> GetNpcAmbushForGoal(uint questDefinitionId, uint goalId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.NpcAmbushes.FirstOrDefaultAsync(a => a.QuestDefinitionId == questDefinitionId && a.GoalId == goalId);
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
				// #766: the creature's ambient idle animations. Lazy loading is off, so without this every NPC reads an
				// EMPTY set and stands to attention - the exact symptom the ticket reported, one layer down.
				.ThenInclude(t => t.AmbientAnimations)
				// #741: the creature TYPES this one walks with. Same reason as the line above - lazy loading is off, so
				// without this every follower reads an EMPTY set and keeps its own path.
				.Include(n => n.NpcTemplate)
				.ThenInclude(t => t.Follows)
				.Include(n => n.NpcTemplate)
				.ThenInclude(t => t.Chatter)
				// And the bark's PARAMETER slots, or the session slot never reaches the wire (#691). Lazy loading is
				// off, so without this `NpcChatterColumns.ToBark` reads an EMPTY `row.Parameters` and composes a
				// remoteformat carrying NO `<parameter>` elements at all - which is a perfectly well-formed bark, so
				// nothing throws. The client then has nothing to substitute and renders the macro literally: "You
				// should consider joining a guild, {DetectedChar.name}!". #691 filled that slot per RECIPIENT, which
				// cannot help while the slot is dropped a layer earlier, here at load.
				.ThenInclude(c => c.Parameters)
				.Include(n => n.NpcTemplate)
				.ThenInclude(t => t.StatCore)
				.Include(n => n.PatrolNodes)
				.ThenInclude(pn => pn.Waypoints)
				// Words too - lazy loading is off, and PatrolNodeMapper throws on a short word set rather than
				// rebuilding a wrong record, so a missing Include would break every patrol (#410).
				.Include(n => n.PatrolNodes)
				.ThenInclude(pn => pn.Words)
				.Where(n => n.MapId == mapId && (forClass == null || n.ClassId == null || n.ClassId == forClass))
				// EIGHT collection navigations off one root. As a single query that is one JOIN whose row count is their
				// PRODUCT - every morph repeated once per equipment slot per spell per chatter line per patrol word - and
				// EF then dedups it client-side. It cost 2.3s to return Tortage beach's 95 spawns, which is the bulk of
				// the first-entry stall on a map (the zone load is memoized, so it is paid once per map per process).
				// Split, each collection is its own flat read.
				.AsSplitQuery()
				.ToArrayAsync();
		}

		public async Task<NpcSpell[]> GetNpcSpellsByName(string name)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var lowered = name.ToLowerInvariant();
			return await ctx.NpcSpells.Where(s => s.NpcTemplate.Name.ToLower() == lowered).ToArrayAsync();
		}

		// NPC TEMPLATE id -> the quest ids that reveal it (#261). ⚠️ Membership is the goal's own GatesTargetVisibility
		// flag, NOT "a Kill goal that happens to carry a target". The gate forces NoRespawn, so implying it from the
		// target made every mechanically-resolved kill target (#521) hide its whole population and stop respawning -
		// see QuestGoalDefinition.GatesTargetVisibility for the quest that broke.
		// ⚠️ The flag decides WHICH NPCs are gated; it does NOT decide which quests REVEAL them. Those are two questions
		// and answering both with `GatesTargetVisibility` softlocked the Casilda chain (#574). Scavenger Tuut is gated
		// by 13479 "The Scavenger" (Kill, the flagged goal), but 13563 "The Scavenger's Key" (Loot) targets him too -
		// and killing him COMPLETES 13479, the only quest that was revealing him. He is also NoRespawn, a story kill
		// target that stays dead. So the moment his loot bag went (expiry, or a server restart - bags are live world
		// state and are not persisted), Tuut was invisible for good, the key was unobtainable, and Casilda could never
		// be freed. Reported in-client 2026-08-02: "killed tuut but did not loot ... she is still locked".
		//
		// So an NPC is gated if ANY goal targeting it is flagged, and once gated it is revealed by EVERY quest that
		// targets it - which is what "this NPC still has business with you" actually means. Widening only the reveal
		// set keeps the flag's original job intact: a mechanically-resolved kill target (#521) that nothing flags
		// still gates nothing, so it does not hide its population or stop respawning.
		//
		// ⚠️ It USED TO key on the name, which was a live bug (#521). `Saddur` is TWO templates - 2993 on the Stygian
		// Galley and 9500 on Tortage Island - and goal 3490 flags the Tortage one. Keying the gate on the string
		// therefore stamped GateQuestIds + NoRespawn onto the GALLEY Saddur as well, hiding an NPC on the intro
		// playfield from every player who was not carrying a Tortage Island quest they cannot have yet. A template is
		// exactly one kind of NPC; a display name is ambiguous in the world (`Halen van Dummy` is 27 of them), which is
		// the same reason ConversationNode re-keyed in #646. Both sides moved together: the giver set reads the node's
		// NpcTemplateId, not its NpcName label.
		//
		// A goal whose target has not RESOLVED to a template (TargetTemplateId null - 586 of them, naming NPCs on maps
		// that have not been imported) is simply not in this map. That is correct on both sides: it cannot gate an NPC
		// that does not exist here, and it cannot reveal one either. `SeedContentTests.EveryGatedGoal_ResolvesToATemplate`
		// keeps the FLAGGED goals out of that bucket, since a flag on an unresolved goal would be silently inert.
		public async Task<Dictionary<uint, uint[]>> GetQuestGatedNpcMap()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var givers = (await ctx.ConversationEdges.Where(e => e.GrantQuestId > 0).Select(e => e.From.NpcTemplateId).Distinct().ToListAsync()).ToHashSet();

			var targeted = await ctx
				.QuestGoalDefinitions.Where(g => g.TargetTemplateId != null)
				.Join(ctx.QuestDefinitions, g => g.QuestDefinitionId, d => d.QuestId, (g, d) => new
				{
					TargetTemplateId = g.TargetTemplateId!.Value,
					d.QuestId,
					g.GatesTargetVisibility
				})
				.Distinct()
				.ToListAsync();

			var gated = targeted.Where(x => x.GatesTargetVisibility).Select(x => x.TargetTemplateId).ToHashSet();

			return targeted.Where(x => gated.Contains(x.TargetTemplateId) && !givers.Contains(x.TargetTemplateId))
				.GroupBy(x => x.TargetTemplateId)
				.ToDictionary(g => g.Key, g => g.Select(x => x.QuestId).Distinct().ToArray());
		}

		// The NPC TEMPLATES a quest goal kills ONCE (`QuestGoalDefinition.TargetStaysDead` -> WorldEntity.NoRespawn).
		// A separate question from the visibility gate above and a separate query for that reason (#708): the two used
		// to be one column, so a quest-only target that must respawn - a repeatable or retryable quest - could not be
		// expressed at all.
		//
		// ⚠️ NO widening here, unlike the gate. The gate deliberately reveals a gated NPC to every quest that targets
		// it (#574); staying dead is a property of the TARGET, so it reads the flagged goals and nothing more. A goal
		// whose target has not resolved to a template is absent from both, which is right: it cannot pin down an NPC
		// that does not exist here.
		public async Task<HashSet<uint>> GetStoryKillTargets()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return (await ctx
					.QuestGoalDefinitions.Where(g => g.TargetStaysDead && g.TargetTemplateId != null)
					.Select(g => g.TargetTemplateId!.Value)
					.Distinct()
					.ToListAsync())
				.ToHashSet();
		}

		// Which quest each NPC TEMPLATE gives, derived from the dialogue graph's own grants (#646: keyed by template,
		// like everything else that routes off a click - the display name it used to key on is ambiguous in the world).
		public async Task<Dictionary<uint, uint>> GetQuestGiverMap()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var grants = await ctx
				.ConversationEdges.Where(e => e.GrantQuestId > 0)
				.Select(e => new
				{
					e.From.NpcTemplateId,
					e.GrantQuestId
				})
				.Distinct()
				.ToListAsync();
			return grants.GroupBy(g => g.NpcTemplateId).ToDictionary(g => g.Key, g => g.Min(x => x.GrantQuestId!.Value));
		}

		// #647: which templates carry a ROLE, straight off the client's own `NpcStats.profession_type`. This is what
		// makes "is the NPC I clicked an auctioneer" answerable for every auctioneer in the world rather than for the
		// one template a capture happened to see, and without matching on a display name that is a role rather than an
		// identity ("Trader" is four templates here and would be any NPC that shared the string).
		public async Task<uint[]> GetNpcTemplatesByProfession(NpcProfession profession)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.NpcStatCores.Where(c => c.ProfessionType == (uint)profession)
				.Select(c => c.NpcTemplateId)
				.ToArrayAsync();
		}

		// The ambient spawn-area pools (#363) and the creature TYPE each one spawns. The include set has to match what
		// the type-built spawn body needs, because lazy loading is off: an APPEARANCE navigation that is not Included
		// arrives EMPTY and the builder cheerfully emits a creature with no morph table and no worn equipment. That is
		// #835 - every ambient NPC in the world rendered naked, and it stayed naked after the type rows were filled in,
		// because the rows were never loaded. `GetNpcByTemplateId` / `GetNpcByName` (the pet and encounter paths) have
		// always Included both; this one had not.
		public async Task<NpcSpawnArea[]> GetResolvedSpawnAreas(uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.NpcSpawnAreas.Include(a => a.NpcTemplate)
				.ThenInclude(t => t!.StatCore)
				.Include(a => a.NpcTemplate)
				.ThenInclude(t => t!.Spells)
				.Include(a => a.NpcTemplate)
				.ThenInclude(t => t!.Equipment)
				.ThenInclude(e => e.Item) // the builder reads the worn item's identity through this navigation
				.Include(a => a.NpcTemplate)
				.ThenInclude(t => t!.Morphs)
				.Include(a => a.Points)
				// Five collection navigations off one root would be returned as their PRODUCT and deduped client-side
				// (#582). Nothing here depends on the un-ordered read order.
				.AsSplitQuery()
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
