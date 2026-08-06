using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// A quest-triggered AMBUSH: an NPC that does not stand in the world at all until a player advances a particular
	// quest goal, at which point it appears beside them and attacks (#528). The third trigger shape, alongside
	// quest-gated VISIBILITY (#261/#262 - the NPC is there, only some players see it) and the proximity lure
	// (`NpcEncounter` - the NPC is there and turns into something else).
	//
	// Decoded from `xxx_mage-necromancer_login_play2_tortage_city_first-quests`: picking up the first Quarry
	// Stonebrick for quest 12371 ("Get 3 stone bricks") grants the item at t=315.6 and a `Bat Demon` spawns at
	// t=318.6, 3.6 u from the brick, arriving with the player already on its hate list. It is the ONLY genuine
	// mid-session NPC spawn in that capture - every other NPC arrives in the world-entry stream - and it despawns
	// again at t=372.9.
	//
	// HAND-AUTHORED, not harvested, exactly as `NpcEncounter` is: nothing on the wire states the trigger or the
	// delay, so they are read off one capture's timeline and written here.
	//
	// ⚠️ Keyed by (map, ambusher type) rather than by the quest, because the load path's question is "does this
	// PLACEMENT belong to an ambush?" - a `NpcSpawns` row for an ambusher is a harvest of a sprung ambush, not a
	// creature that stands there, and `WorldBroadcaster.LoadNpcs` skips it on that lookup. Keying by the goal would
	// make that a scan.
	[PrimaryKey(nameof(MapId), nameof(NpcTemplateId))]
	public class NpcAmbush
	{
		public uint MapId { get; set; }

		[ForeignKey(nameof(MapId))]
		public virtual Map Map { get; set; } = null!;

		// What springs. A TYPE, not a placement - it is spawned from its stat core the way an encounter boss is.
		public uint NpcTemplateId { get; set; }

		[ForeignKey(nameof(NpcTemplateId))]
		public virtual NpcTemplate NpcTemplate { get; set; } = null!;

		// The goal whose FIRST advance springs it (12371 / 3495 - "Get 3 stone bricks"). Only the first: the captured
		// session picks up all three bricks and gets exactly one Bat Demon.
		public uint QuestDefinitionId { get; set; }

		[ForeignKey(nameof(QuestDefinitionId))]
		public virtual QuestDefinition QuestDefinition { get; set; } = null!;

		public uint GoalId { get; set; }

		[ForeignKey(nameof(GoalId))]
		public virtual QuestGoalDefinition Goal { get; set; } = null!;

		// How long after the goal advances the body arrives. The trigger EFFECTS are immediate at the pickup and the
		// NPC follows ~3 s later - two distinct steps, not one spawn.
		public float DelaySeconds { get; set; }

		// How far from the player it lands, in world units. Retail anchors on the interacted OBJECT (3.6 u from the
		// brick); we anchor on the player standing at it, which is the same spot to within the interaction range.
		public float AnchorDistance { get; set; }
	}
}
