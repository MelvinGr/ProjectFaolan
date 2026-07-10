using System.Collections.Generic;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// The structured form of a player SimpleCharFullUpdate (FA015807) body: everything around the embedded
	// ProtoCoreCharacterIIR, per the validated grammar in docs/re/player-spawn-generation-plan.md ("VALIDATED
	// GRAMMAR"). Parse + Generate round-trip byte-exact over every captured player-own spawn (proven by
	// Captures/scripts/parse_spawn_scaffold.py over all 34, locked here by PlayerSpawnScaffoldCodecTests on the
	// representative golden fixtures). This is what lets the spawn scaffold live as DB rows instead of the
	// captured template/burst bytes.
	public sealed class PlayerSpawnScaffold
	{
		// The embedded ProtoCoreCharacterIIR bytes (typed round-trip via FromProtobuf/ToProtobufBytes is
		// byte-identical; see PlayerSpawnGenerationTests).
		public byte[] CharacterIir { get; set; }

		// The pre-IIR frame between the transform and the CharacterIIR length prefix: a near-constant skeleton
		// with per-character value islands (228 bytes editor-constant / 239 normal / 339 geared+table). Kept raw;
		// PlayerSpawnScaffoldRows.FreshPreFrame builds the fresh-character variant.
		public byte[] PreFrame { get; set; }

		public byte[] Quat { get; set; } // Todo: convert to vector3 or 3 floats
		public byte[] Scale { get; set; } // null when absent

		// Transform component payloads (the 0x2C group), kept as raw field payloads: 0x0A vec3 (3 x fixed32),
		// 0x12 quat (4 x fixed32), optional 0x25 scale (4 bytes), 0x18 playfield varint.
		public byte[] Vec3 { get; set; } // Todo: convert to vector3 or 3 floats

		// Post-IIR stream. Buff/timer records are 60-byte transient state (only on captured chars with active
		// buffs); the entry table / ID list / end pairs are the per-character skill+feat scaffold.
		public List<byte[]> BuffRecords { get; } = [];
		public List<ScaffoldEntry> Entries { get; } = [];

		public List<ScaffoldIdEntry> IdList { get; } = [];

		// EndPairs (the first ending list) IS the FACTION-REPUTATION block: Id = faction id, Value = signed standing
		// (+/-50000) shown in the client's Factions window. See CharacterScaffoldPair / Gitea #315.
		public List<ScaffoldPair> EndPairs { get; } = [];
		public List<ScaffoldPair> EndPairs2 { get; } = [];
		public uint Ack { get; set; } = EntityClass.Character;

		public uint EntryTableX { get; set; } // per-char u32 after the entry count (Strie 0xB7, necrol80 0x2B6, editor 0x3F1)
		public uint FieldA { get; set; }
		public uint Handle { get; set; }
		public ulong Playfield { get; set; }
	}

	// One uniform 14-byte entry [u32 index][u32 0][u16 tag][u32 value]: the weighted attributes read as
	// tag=value/value=0x3F800000 (weight 1.0), the feats as tag=(type<<8|subindex)/value=float bits.
	public readonly record struct ScaffoldEntry(uint Index, ushort Tag, uint Value);

	// One 5-byte ID-list group [u8 rank][u32 id]; ids ascend, the id 0x3F1 terminates the list on the wire.
	public readonly record struct ScaffoldIdEntry(byte Rank, uint Id);

	// One 8-byte ending pair [u32 id][s32 value] (ids are decades 10..110).
	public readonly record struct ScaffoldPair(uint Id, int Value);
}
