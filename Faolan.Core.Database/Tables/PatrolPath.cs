using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// A captured NPC patrol route - the MoveTaskIIR (0x96C46740) task tree, columnarized as a PatrolNode tree (#316,
	// the full recursive AST). Belongs to ONE NpcSpawn (the NPC that walks it) via NpcSpawnId - the importer
	// correlates route -> spawn by their shared transform (the spawn position == the MoveTask anchor, see
	// npc_spawn_movetask_position) at import time, then stores the FK, so the runtime joins a spawn to its route
	// directly (no float-position match). MoveTaskBuilder.BuildPatrol re-emits the tree byte-exact, re-addressed to
	// the live handle + spawn position. Map + anchor position live on the NpcSpawn. Imported from captures by
	// Faolan.Importer (PatrolPathExtractor); cascade-deletes with its spawn.
	// Keyed BY its spawn: the relationship is 1:1 (an NPC walks one route), so NpcSpawnId is both the foreign key
	// and the whole identity - a surrogate alongside it identified nothing and renumbered on every re-harvest.
	// Keyed BY its spawn: the relationship is 1:1 (an NPC walks one route), so NpcSpawnId is both the foreign key
	// and the whole identity - a surrogate alongside it identified nothing and renumbered on every re-harvest.
	[PrimaryKey(nameof(NpcSpawnId))]
	public class PatrolPath
	{
		public uint NpcSpawnId { get; set; }

		[ForeignKey(nameof(NpcSpawnId))]
		public virtual NpcSpawn NpcSpawn { get; set; } = null!;

		// The columnarized MoveTask task tree: every AST node of this patrol (the root has ParentId == null).
		// Cascade-deletes with the path.
		public virtual ICollection<PatrolNode> Nodes { get; set; } = [];
	}
}
