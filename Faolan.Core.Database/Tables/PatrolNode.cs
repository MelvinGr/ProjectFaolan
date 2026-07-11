using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// One node of a patrol's MoveTaskIIR (0x96C46740) task tree, columnarized from the captured record (#316). The
	// tree is a recursive mode-polymorphic AST (a mode-4 ListMoveTask of Path/Wait/Jump/nested-List children); this
	// self-referencing table stores it row-per-node so the runtime CONSTRUCTS the patrol from DB columns instead of
	// replaying the captured `PatrolPath.Body` blob. The mapping to/from Faolan.Core.Network's MoveTaskNode is
	// PatrolNodeMapper; MoveTaskBuilder.BuildPatrol re-emits the byte-exact record (PatrolNodeRoundTripTests over
	// every committed patrol). Only mode-specific fields are populated (null otherwise); the fixed numeric arrays
	// that don't columnize (Path's middle trailer words, Ballistic/Formation/Vec words, the approach tail) ride in
	// WordsData. Floats confirmed as floats (duration/remaining/speed/anchor/waypoints) are stored as floats; slots
	// of unknown numeric type stay raw u32 bits so the record round-trips exactly.
	public class PatrolNode : BaseEntity
	{
		public uint PatrolPathId { get; set; }

		[ForeignKey(nameof(PatrolPathId))]
		public virtual PatrolPath PatrolPath { get; set; }

		// Self-reference: null for the root task, else the parent ListMoveTask. Ordinal is the child order.
		public uint? ParentId { get; set; }

		[ForeignKey(nameof(ParentId))]
		public virtual PatrolNode Parent { get; set; }

		public virtual ICollection<PatrolNode> Children { get; set; } = new List<PatrolNode>();

		public int Ordinal { get; set; }

		// MoveTaskNode.Mode (0 Wait, 1 Path, 2 Rotate, 3 Jump, 4 List, 5 Ballistic, 7 Formation, 8 Approach, 10 SetPos, 11 GroundAlign).
		public byte Mode { get; set; }

		// WaitMoveTask base header (modes 0/1/4/5): flags + (duration/remaining floats when flags&1 == 0).
		public byte? HeaderFlags { get; set; }
		public float? Duration { get; set; }
		public float? Remaining { get; set; }

		// mode 4 ListMoveTask
		public byte? ListMin { get; set; }

		// mode 1 PathMoveTask
		public byte? PathFlags { get; set; }
		public uint? PathFloat10 { get; set; } // present iff PathFlags & 0x10 (raw u32 bits - semantics unknown)
		public uint? PathFloat20 { get; set; } // present iff PathFlags & 0x20
		public int? PathPacked1 { get; set; } // PathFlags & 1 branch (u16 each)
		public int? PathPacked2 { get; set; }
		public byte? TrailerByte { get; set; }

		// Path trailer semantic words (the 7-word trailer decodes as [speed, pad, dir, dir, anchorX, anchorY, anchorZ]).
		public float? Speed { get; set; } // trailer word 0
		public float? AnchorX { get; set; } // trailer word 4
		public float? AnchorY { get; set; } // trailer word 5
		public float? AnchorZ { get; set; } // trailer word 6

		// modes 2 Rotate / 3 Jump / 5 Ballistic / 11 GroundAlign scalar.
		public byte? Flag { get; set; }
		public int? RotateTail { get; set; } // mode 2 trailing u16

		// mode 8 Approach
		public uint? ApproachType { get; set; }
		public uint? ApproachTarget { get; set; }

		// The node's fixed numeric-array payload that doesn't columnize, as raw big-endian bytes: Path's 3 middle
		// trailer words (pad + 2 dir), Ballistic's 7 words, Formation's 6, Rotate/SetPos's 7 (Vec7), Approach's 10-byte
		// tail. Null when the mode has none.
		public byte[] WordsData { get; set; }

		// mode 1 PathMoveTask else-branch waypoints (empty for other modes / the packed branch).
		public virtual ICollection<PatrolNodeWaypoint> Waypoints { get; set; } = new List<PatrolNodeWaypoint>();
	}
}
