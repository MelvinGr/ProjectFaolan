using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// A map's captured login world-build (MapChange + world objects + entity spawns + inventory), replayed on world
	// entry and re-addressed from CapturedInstance to the live character. Generalizes the embedded beach world-burst
	// to any map. Imported by Faolan.Importer. The burst is stored as STRUCTURED IIR records (MapWorldBurstEntity),
	// not opaque packet blobs - see IirRecordCodec.
	[Obsolete("ALL PACKETS NEED TO BE BUILD FROM SCRATCH DB BACKED")]
	public class MapWorldBurst : BaseEntity
	{
		// The ordered, decomposed IIR records that make up the world build (see MapWorldBurstEntity / IirRecordCodec).
		public virtual ICollection<MapWorldBurstEntity> Entities { get; set; }

		public uint CapturedInstance { get; set; }
		public uint MapId { get; set; }
	}

	// One decomposed packet of a map's world burst. The AoC world build is a stream of IIR (Incremental Information
	// Replication) records, each `RpcHeader + 0x2000 + [u32 len][u32 IirType][u32 Const][u32 Handle][u32 FieldA][body]`
	// (or another opcode, e.g. the 0x2076 entity-index manifest). Rather than storing the whole opaque packet, the
	// semantic header (the IIR type, its class tag, the target entity handle) is lifted into columns; the framing
	// (RpcHeader + opcode marker, which carries the source endpoint + instance) is kept verbatim in Header, and the
	// not-yet-dissected component payload in Body. IirRecordCodec.Rebuild reconstructs the exact original bytes
	// (proven round-trip-lossless over all captured maps), so the proven byte-preserving replay path is unchanged.
	// Field-level dissection (Body -> typed columns) proceeds per IIR type; see docs/archive/spawn-dissection-plan.md.
	[Obsolete("ALL PACKETS NEED TO BE BUILD FROM SCRATCH DB BACKED")]
	public class MapWorldBurstEntity : BaseEntity
	{
		[ForeignKey(nameof(MapWorldBurstId))]
		public virtual MapWorldBurst Burst { get; set; }

		// The IIR component payload (everything after the lifted header fields), verbatim until dissected.
		public byte[] Body { get; set; }

		// --- raw remainder (shrinks as components are dissected into typed columns) ------------------------------
		// RpcHeader + the 2-byte opcode marker, verbatim. Carries the source endpoint + (re-addressable) instance.
		public byte[] Header { get; set; }

		// Order within the burst (the replay sends in this order).
		public int Sequence { get; set; }

		// The field following the handle (usually 0; a stat id for StatIIR, a sub-count for FullChar, etc.).
		public uint FieldA { get; set; }

		// The target entity handle this record addresses (0 for global records like MapChange / GameTime).
		public uint Handle { get; set; }

		// --- IIR header (populated when Opcode == 0x2000) -------------------------------------------------------
		// The IIR message type (sub-opcode), e.g. 0xFA015807 SimpleCharFullUpdate, 0x66AEDD50 SimpleDynelFullUpdate.
		public uint IirType { get; set; }

		public uint MapWorldBurstId { get; set; }

		// The per-type class/version tag that precedes the handle (0xC350 char, 0xC878 dynel, 0xC87A lootbag, ...).
		public uint TypeTag { get; set; }

		// The packet opcode at the marker: 0x2000 for an IIR record, otherwise a framing opcode (e.g. 0x2076 manifest).
		public ushort Opcode { get; set; }
	}
}
