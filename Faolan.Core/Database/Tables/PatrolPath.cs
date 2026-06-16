using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// A captured NPC patrol route - the MoveTaskIIR (0x96C46740) task-type-0x04 waypoint form. Keyed by
	// (MapId, Position): Position is the patrol's current/anchor point, which equals the paired NpcSpawn's spawn
	// position (the spawn and its MoveTask share a transform - see npc_spawn_movetask_position), so the live
	// MoveTask the server builds for a spawn can source its waypoints from here by position (construct-don't-replay,
	// #53). Speed + the ordered Waypoints reproduce the captured patrol body byte-exact via
	// MoveTaskBuilder.BuildPatrol. Imported from captures by Faolan.Importer (PatrolPathExtractor). Only the
	// canonical single-segment patrol is stored; multi-segment / parametric patrols stay the documented MoveTask
	// remainder (dissect_movetask.py). Position columns are inherited from Base3DEntity (rotation is unused).
	public class PatrolPath : Base3DEntity
	{
		public uint MapId { get; set; }

		// Walk speed - the patrol move-block trailer's big-endian float.
		public float Speed { get; set; }

		public virtual ICollection<PatrolWaypoint> Waypoints { get; set; } = new List<PatrolWaypoint>();

		[ForeignKey(nameof(MapId))]
		public virtual Map Map { get; set; }
	}
}
