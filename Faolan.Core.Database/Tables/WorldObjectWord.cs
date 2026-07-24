using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// Which array a WorldObjectWord belongs to. Both live in one table because both are plain u32 sequences read
	// from the same record - the kind is what says where in it they sit.
	public enum WorldObjectWordKind : byte
	{
		// A node of the object's kd-tree/bounds node list, 6 words per node (empty for all but 24 captured rows).
		GeometryNode = 0,

		// The PER-CLASS trailer after the geometry record: 5 words for Destructible, 1 for Door and Climbing,
		// none for SimpleDynel. See WorldObjectGeometryCodec.
		Trailer = 1
	}

	// One 32-bit word of a [[WorldObject]]'s geometry record, in wire order. Keyed by the natural composite
	// (WorldObjectId, Kind, Ordinal) - like [[PatrolNodeWord]] - and cascade-deleted with its object.
	//
	// Replaces part of the KdTreeTail blob (#410). Values stay RAW u32 BITS rather than floats: the node words'
	// types are not known (the node list is empty in 1028 of 1052 rows, so the corpus barely constrains them), and
	// the record has to round-trip bit-for-bit.
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
