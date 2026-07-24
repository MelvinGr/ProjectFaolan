using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One node of a patrol's MoveTaskIIR (0x96C46740) task tree, columnarized from the captured record (#316). The
	// tree is a recursive mode-polymorphic AST (a mode-4 ListMoveTask of Path/Wait/Jump/nested-List children); this
	// self-referencing table stores it row-per-node so the runtime CONSTRUCTS the patrol from DB columns instead of
	// replaying the captured `PatrolPath.Body` blob. The mapping to/from Faolan.Core.Network's MoveTaskNode is
	// PatrolNodeMapper; MoveTaskBuilder.BuildPatrol re-emits the byte-exact record (PatrolNodeRoundTripTests over
	// every committed patrol). Only mode-specific fields are populated (null otherwise); the fixed numeric arrays
	// (Path's middle trailer words, Ballistic/Formation/Vec words) are one row per word in [[PatrolNodeWord]] and
	// the approach tail is ApproachFlag. Floats confirmed as floats (duration/remaining/speed/anchor/waypoints) are
	// stored as floats; slots of unknown numeric type stay raw u32 bits so the record round-trips exactly.
	// Keyed by WHERE IT SITS IN THE TREE: (path, OrdinalPath). A MoveTask node is a recursive AST, so `Ordinal` is
	// only unique among its SIBLINGS - (PatrolPathId, Ordinal) collides 608 -> 407. OrdinalPath folds the whole
	// ancestor chain into that ordinal ("" = the root, "1" = its second child, "1>0" = that child's first), which
	// IS unique (608/608 verified, max depth 2) and, unlike the surrogate it replaces, is reproduced exactly by a
	// re-harvest. Same device as ConversationNode.AnswerPath.
	// Keyed by WHERE IT SITS IN THE TREE: (path, OrdinalPath). A MoveTask node is a recursive AST, so `Ordinal` is
	// only unique among its SIBLINGS - (PatrolPathId, Ordinal) collides 608 -> 407. OrdinalPath folds the whole
	// ancestor chain into that ordinal ("" = the root, "1" = its second child, "1>0" = that child's first), which
	// IS unique (608/608, max depth 2) and, unlike the surrogate it replaces, is reproduced by a re-harvest.
	// Same device as ConversationNode.AnswerPath.
	[PrimaryKey(nameof(PatrolPathId), nameof(OrdinalPath))]
	public class PatrolNode
	{

		// This node's position within its path: the chain of sibling ordinals from the root.
		[MaxLength(64)]
		public string OrdinalPath { get; set; } = "";

		// Self-reference: the parent's OrdinalPath (this node's own, minus its last segment). NULL for the root
		// task, else the parent ListMoveTask. Replaces the surrogate ParentId; the tree shape is unchanged.
		[MaxLength(64)]
		public string? ParentPath { get; set; }

		[ForeignKey($"{nameof(PatrolPathId)},{nameof(ParentPath)}")]
		public virtual PatrolNode? Parent { get; set; }

		public uint PatrolPathId { get; set; }

		[ForeignKey(nameof(PatrolPathId))]
		public virtual PatrolPath PatrolPath { get; set; } = null!;

		public virtual ICollection<PatrolNode> Children { get; set; } = [];

		// mode 1 PathMoveTask else-branch waypoints (empty for other modes / the packed branch).
		public virtual ICollection<PatrolNodeWaypoint> Waypoints { get; set; } = [];

		// The remaining un-named numeric arrays, one row per 32-bit word in wire order (#410). Modes 1/2/10 have
		// typed columns above now, so this covers only mode 5 Ballistic (7 words) and mode 7 Formation (6) - neither
		// of which has a single node in the corpus, which is exactly why their words stay unnamed.
		public virtual ICollection<PatrolNodeWord> Words { get; set; } = [];

		// MoveTaskNode.Mode (0 Wait, 1 Path, 2 Rotate, 3 Jump, 4 List, 5 Ballistic, 7 Formation, 8 Approach, 10 SetPos, 11 GroundAlign).
		public byte Mode { get; set; }

		// modes 2 Rotate / 3 Jump / 5 Ballistic / 11 GroundAlign scalar.
		public byte? Flag { get; set; }

		// WaitMoveTask base header (modes 0/1/4/5): flags + (duration/remaining floats when flags&1 == 0).
		public byte? HeaderFlags { get; set; }

		// mode 4 ListMoveTask
		public byte? ListMin { get; set; }

		// mode 1 PathMoveTask
		public byte? PathFlags { get; set; }
		public byte? TrailerByte { get; set; }

		// mode 8 Approach's tail, which is [u8 flag][9 zero bytes] - only the flag carries anything, the padding is
		// re-emitted by the builder. Null for every other mode.
		public byte? ApproachFlag { get; set; }
		public float? AnchorX { get; set; } // trailer word 4
		public float? AnchorY { get; set; } // trailer word 5
		public float? AnchorZ { get; set; } // trailer word 6
		public float? Duration { get; set; }
		public float? Remaining { get; set; }

		// Path trailer semantic words (the 7-word trailer decodes as [speed, pad, dir, dir, anchorX, anchorY, anchorZ]).
		public float? Speed { get; set; } // trailer word 0

		// mode 1 Path: the two DIRECTION words of the middle trailer (words 2-3 of the 7-word trailer), decoded as
		// floats - the corpus has them at 0 in most nodes and small non-zero values elsewhere.
		public float? PathDirection1 { get; set; }
		public float? PathDirection2 { get; set; }

		// mode 1 Path: the trailer word BEFORE those two (word 1). The 7-word trailer's shape was written down as
		// [speed, pad, dir, dir, anchor x/y/z] but this slot is NOT padding - it varies 0..41 across the corpus. It
		// is not the waypoint count either (checked: it matches on only 17 of 234 nodes), so it keeps a positional
		// name and its raw bits until something identifies it.
		public uint? PathTrailerWord { get; set; }

		// modes 2 Rotate / 10 SetPos: the 7-word "Vec7" is a TRANSFORM - a position and a unit quaternion. Verified
		// on every captured node: e.g. (1147.29, 199.54, 1758.06) + (0, -0.204, 0, 0.979), and both quaternions
		// normalise to 1.0 (they are rotations about Y, as an NPC facing change would be).
		public float? TargetX { get; set; }
		public float? TargetY { get; set; }
		public float? TargetZ { get; set; }
		public float? TargetRotationX { get; set; }
		public float? TargetRotationY { get; set; }
		public float? TargetRotationZ { get; set; }
		public float? TargetRotationW { get; set; }

		public int Ordinal { get; set; }
		public int? PathPacked1 { get; set; } // PathFlags & 1 branch (u16 each)
		public int? PathPacked2 { get; set; }
		public int? RotateTail { get; set; } // mode 2 trailing u16
		public uint? ApproachTarget { get; set; }

		// mode 8 Approach
		public uint? ApproachType { get; set; }
		public uint? PathFloat10 { get; set; } // present iff PathFlags & 0x10 (raw u32 bits - semantics unknown)
		public uint? PathFloat20 { get; set; } // present iff PathFlags & 0x20
	}
}
