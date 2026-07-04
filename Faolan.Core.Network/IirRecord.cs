namespace Faolan.Core.Network
{
	// One decomposed packet of a world-record (IIR - Incremental Information Replication) stream: the semantic header
	// (the IIR type, its class tag, the target entity handle) lifted into fields, the framing (RpcHeader + opcode
	// marker, which carries the source endpoint + instance) kept verbatim in Header, and the not-yet-dissected
	// component payload in Body. IirRecordCodec.Parse/Rebuild round-trips it byte-for-byte.
	//
	// A plain DTO: this WAS the MapWorldBurstEntity table row, but the stored-burst tables were removed once world
	// entry became constructed (burst-less) rather than replayed. IirRecordCodec + its tests still use this shape to
	// decompose/verify the generated world-entry records.
	public sealed class IirRecord
	{
		// RpcHeader + the 2-byte opcode marker, verbatim. Carries the source endpoint + (re-addressable) instance.
		public byte[] Header { get; set; }

		// The IIR component payload (everything after the lifted header fields), verbatim until dissected.
		public byte[] Body { get; set; }

		// Order within the stream (the replay/verify sends in this order).
		public int Sequence { get; set; }

		// The target entity handle this record addresses (0 for global records like MapChange / GameTime).
		public uint Handle { get; set; }

		// The field following the handle (usually 0; a stat id for StatIIR, a sub-count for FullChar, etc.).
		public uint FieldA { get; set; }

		// The IIR message type (sub-opcode), e.g. 0xFA015807 SimpleCharFullUpdate, 0x66AEDD50 SimpleDynelFullUpdate.
		public uint IirType { get; set; }

		// The per-type class/version tag that precedes the handle (0xC350 char, 0xC878 dynel, 0xC87A lootbag, ...).
		public uint TypeTag { get; set; }

		// The packet opcode at the marker: 0x2000 for an IIR record, otherwise a framing opcode (e.g. 0x2076 manifest).
		public ushort Opcode { get; set; }
	}
}
