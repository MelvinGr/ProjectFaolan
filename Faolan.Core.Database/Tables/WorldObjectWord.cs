using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// Which array a WorldObjectWord belongs to. Both live in one table because both are plain u32 sequences read
	// from the same record - the kind is what says where in it they sit.
	public enum WorldObjectWordKind : byte
	{
		// A node of the object's kd-tree/bounds node list, 6 words per node. NOT kd-tree geometry (#687): it is a
		// STANDING PARTICLE EFFECT and the bone it hangs on -
		//
		//     [effect] [0] [0] [0] [0] [attractor]
		//
		// `effect` (ordinal 0) is an RDB **1010207** record - the particle-effect type. All 11 shipped ids resolve
		// there in the client's own RDB, checked one by one. `attractor` (ordinal 5) is the attachment point, the
		// same slot `SimpleDynelVisualChangeCodec.StartParticleEffect` carries: it takes exactly {34, 35} and no
		// other value, and it is CONSTANT per effect id (4998690 and 3139195 always hang on 34, the other nine
		// always on 35) - which is what an attachment point is and what a coordinate could not be.
		//
		// ⚠️ It is NOT the visual-change payload with its columns rotated, which is what #687 set out to check. That
		// payload's order is client-confirmed from the reader itself (the `if (attractor < 1)` branch):
		// `[effect][attractor][rotation][x][y][z]`, attractor at index **1**. Here it is at index **5**. Two
		// different layouts over the same six semantic fields, not one layout read two ways.
		//
		// ⚠️ The middle four (ordinals 1-4) are **0 in every one of the 127 shipped nodes**, so which of them is the
		// rotation and which are the offset is CORPUS-UNDECIDABLE. `[effect][four zeros][attractor]` is the whole of
		// what the data supports; naming them would be inventing the order.
		GeometryNode = 0,

		// The PER-CLASS trailer after the geometry record: 5 words for Destructible, 1 for Door and Climbing,
		// none for SimpleDynel. See WorldObjectGeometryCodec.
		Trailer = 1
	}

	// One 32-bit word of a [[WorldObject]]'s geometry record, in wire order. Keyed by the natural composite
	// (WorldObjectId, Kind, Ordinal) - like [[PatrolNodeWord]] - and cascade-deleted with its object.
	//
	// Replaces part of the KdTreeTail blob (#410). Values stay RAW u32 BITS rather than floats: the node words'
	// types are only partly known (see GeometryNode - two of the six are named, the other four are zero throughout),
	// and the record has to round-trip bit-for-bit.
	[PrimaryKey(nameof(WorldObjectId), nameof(Kind), nameof(Ordinal))]
	public class WorldObjectWord
	{
		public uint WorldObjectId { get; set; }

		[ForeignKey(nameof(WorldObjectId))]
		public virtual WorldObject WorldObject { get; set; } = null!;

		public WorldObjectWordKind Kind { get; set; }

		public uint Value { get; set; }

		public int Ordinal { get; set; }
	}
}
