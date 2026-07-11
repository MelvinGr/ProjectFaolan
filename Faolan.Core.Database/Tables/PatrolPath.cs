using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// A captured NPC patrol route - the MoveTaskIIR (0x96C46740) task tree. Keyed by (MapId, Position): Position is
	// the patrol's current/anchor point, which equals the paired NpcSpawn's spawn position (the spawn and its
	// MoveTask share a transform - see npc_spawn_movetask_position), so the live MoveTask the server builds for a
	// spawn sources its route from here by position (construct-don't-replay, #53). The task is stored COLUMNARIZED
	// as a PatrolNode tree (#316) - the full recursive MoveTask AST (single- and multi-segment, all 7 captured
	// forms) - which MoveTaskBuilder.BuildPatrol re-emits byte-exact (re-addressed to the live handle + spawn
	// position), replacing the old verbatim Body blob + PatrolWaypoint list. Imported from captures by
	// Faolan.Importer (PatrolPathExtractor). Position columns are inherited from Base3DEntity (rotation is unused).
	public class PatrolPath : Base3DEntity
	{
		public uint MapId { get; set; }

		[ForeignKey(nameof(MapId))]
		public virtual Map Map { get; set; }

		// The columnarized MoveTask task tree: every AST node of this patrol (the root has ParentId == null).
		// Cascade-deletes with the path.
		public virtual ICollection<PatrolNode> Nodes { get; set; } = new List<PatrolNode>();
	}
}
