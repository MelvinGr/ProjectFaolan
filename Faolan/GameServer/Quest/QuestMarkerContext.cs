using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;

namespace Faolan.GameServer.Quest
{
	// One character's quest state, read ONCE and asked about many NPCs (#939). `QuestMarkerResolver.MarkerStateFor`
	// used to reload all of it per NPC - the character's rows, the active definitions, and one full-graph
	// definition load per quest the character had EVER held - so an entry burst of seven NPCs on a level-80 with
	// 1,565 completions ran ~11,000 queries: 1.7-2.5 s of the login, every login.
	//
	// The batch callers (the entry followups, the interest stream, a spawn poll) build one of these per character
	// per batch and hand it to every marker resolution in it. What a marker needs from a definition is three words -
	// line, level, step - so those come through a projection (`GetQuestLineMembership`), memoised here as the NPCs
	// name new quests; the full definitions are loaded only for the ACTIVE set, whose goals the turn-in check reads.
	public sealed class QuestMarkerContext
	{
		private readonly IWorldRepository _world;
		private readonly Dictionary<uint, QuestLineMembership?> _membership = new Dictionary<uint, QuestLineMembership?>();
		private readonly Dictionary<uint, QuestDefinition[]> _lineSteps = new Dictionary<uint, QuestDefinition[]>();

		private QuestMarkerContext(IWorldRepository world, Character character, CharacterQuest[] quests, (CharacterQuest Quest, QuestDefinition Definition)[] active)
		{
			_world = world;
			Character = character;
			Quests = quests;
			Active = active;
		}

		public Character Character { get; }

		// Every row the character holds, in every state - the giver fold and the line-reached gate read both.
		public CharacterQuest[] Quests { get; }

		// The ACTIVE quests with their definitions (goals included): the turn-in check walks each one's current goal.
		public (CharacterQuest Quest, QuestDefinition Definition)[] Active { get; }

		public static async Task<QuestMarkerContext> Load(ICharacterQuestRepository quests, IWorldRepository world, Character character)
		{
			var held = await quests.GetCharacterQuests(character.Id);
			var active = await world.GetCharacterQuestsWithDefinitions(character.Id, QuestState.Active);
			var context = new QuestMarkerContext(world, character, held, active);
			// The held set is asked about by every ChainRunsThrough, so it is read up front in one projection.
			await context.Memberships(held.Select(q => q.QuestId));
			return context;
		}

		public CharacterQuest? Held(uint questId)
		{
			return System.Array.Find(Quests, q => q.QuestId == questId);
		}

		// The line / level / step of a quest, or null for an id the catalog does not carry.
		public async Task<QuestLineMembership?> Membership(uint questId)
		{
			return (await Memberships([questId])).GetValueOrDefault(questId);
		}

		// Batched: whatever is not memoised yet is fetched in ONE projection query.
		public async Task<IReadOnlyDictionary<uint, QuestLineMembership?>> Memberships(IEnumerable<uint> questIds)
		{
			var wanted = questIds.Distinct().ToArray();
			var missing = wanted.Where(id => !_membership.ContainsKey(id)).ToArray();
			if (missing.Length > 0)
			{
				var loaded = await _world.GetQuestLineMembership(missing);
				foreach (var id in missing)
					_membership[id] = loaded.GetValueOrDefault(id);
			}

			return wanted.ToDictionary(id => id, id => _membership[id]);
		}

		// A line's steps in journal order, for the line-reached gate; one read per line per context.
		public async Task<QuestDefinition[]> LineSteps(uint questLineId)
		{
			if (!_lineSteps.TryGetValue(questLineId, out var steps))
				_lineSteps[questLineId] = steps = await _world.GetQuestDefinitionsByLine(questLineId);

			return steps;
		}
	}
}
