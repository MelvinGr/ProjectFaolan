using System.Collections.Generic;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// The structured form of a player SimpleCharFullUpdate (FA015807) body: everything around the embedded
	// ProtoCoreCharacterIIR, per the validated grammar in docs/re/player-spawn-generation.md ("VALIDATED
	// GRAMMAR"). Parse + Generate round-trip byte-exact over every captured player-own spawn (proven by
	// tools/capture/parse_spawn_scaffold.py over all 34, locked here by PlayerSpawnScaffoldCodecTests on the
	// representative golden fixtures). This is what lets the spawn scaffold live as DB rows instead of the
	// captured template/burst bytes.
	public sealed class PlayerSpawnScaffold
	{
		public List<ScaffoldEntry> Entries { get; } = [];

		public List<ScaffoldIdEntry> IdList { get; } = [];

		// EndPairs (the first ending list) IS the FACTION-REPUTATION block: Id = faction id, Value = signed standing
		// (+/-50000) shown in the client's Factions window. See CharacterScaffoldPair / Gitea #315.
		public List<ScaffoldPair> EndPairs { get; } = [];
		public List<ScaffoldPair> EndPairs2 { get; } = [];

		// A THIRD pair list, and the body's last field. What used to be called "the final 0x3F1 marker" is its count
		// word: 0x3F1 is ForCount(0), and it is empty on all but 10 captured spawns, so it read as a closing constant.
		// Note the asymmetry, which is what the bytes say rather than what is tidy: EndPairs/EndPairs2 are framed by a
		// PLAIN count, this one by a ListFrame word. Every observed element is (975, 0).
		public List<ScaffoldPair> EndPairs3 { get; } = [];

		// The plain-counted list that OPENS the post-IIR stream, before the constant 4. Empty on all but 2 captured
		// spawns (which carry a single word, 60), which is why the pair read as the constant [0][4] for so long.
		public List<uint> HeadWords { get; } = [];

		// Post-IIR stream. Buff/timer records are 60-byte transient state (only on captured chars with active
		// buffs); the entry table / ID list / end pairs are the per-character skill+feat scaffold.
		public List<byte[]> BuffRecords { get; } = [];

		// The embedded ProtoCoreCharacterIIR bytes (typed round-trip via FromProtobuf/ToProtobufBytes is
		// byte-identical; see PlayerSpawnGenerationTests).
		public byte[] CharacterIir { get; set; } = null!;

		// The pre-IIR frame between the transform and the CharacterIIR length prefix: a near-constant skeleton
		// with per-character value islands (228 bytes editor-constant / 239 normal / 339 geared+table). Kept raw;
		// PlayerSpawnScaffoldRows.FreshPreFrame builds the fresh-character variant.
		public byte[] PreFrame { get; set; } = null!;

		public byte[] Quat { get; set; } = null!; // #352: columnize (kept raw - typed would lose the exact captured field-set)

		// Transform component payloads (the 0x2C group), kept as raw field payloads: 0x0A vec3 (3 x fixed32),
		// 0x12 quat (4 x fixed32), optional 0x25 scale (4 bytes), 0x18 playfield varint.
		public byte[] Vec3 { get; set; } = null!; // #352: columnize (kept raw - typed would lose the exact captured field-set)
		public byte[]? Scale { get; set; } // null when absent
		public uint Ack { get; set; } = EntityClass.Character;

		public uint EntryTableX { get; set; } // per-char u32 after the entry count (Strie 0xB7, NecroL80 0x2B6, editor 0x3F1)

		// The rank byte on the ID list's terminating group. Zero on most captured spawns, which is why it was long
		// asserted to BE zero - across the corpus 23 spawns carry a non-zero one, and the parse still consumes to
		// exactly the body's end, so it is a carried value and not a corruption. Round-tripped rather than assumed.
		public byte IdListTerminatorRank { get; set; }

		// The list that CLOSES the ID list. Its count word is what the grammar long called "the 0x3F1 terminator":
		// 0x3F1 is ListFrame.ForCount(0), so on the ~200 spawns whose list is empty the word reads as a constant and
		// the mistake is invisible. 17 captured spawns carry ForCount(1) or ForCount(2) and the parser walked straight
		// into the records, which is the 0x3F1 trap ListFrame documents, hit for a fourth time. Semantics unknown: the
		// [u32 0xC350][u32 id] head is the wire's entity-reference shape and the ids are small (39859, 39860, 43517),
		// so these reference something rather than describing it - carried verbatim until a capture says what.
		public List<ScaffoldTailRef> IdListTail { get; } = [];
		public uint FieldA { get; set; }
		public uint Handle { get; set; }
		public ulong Playfield { get; set; }
	}

	// One uniform 14-byte entry [u32 index][u32 flag][u16 tag][u32 value]: the weighted attributes read as
	// tag=value/value=0x3F800000 (weight 1.0), the feats as tag=(type<<8|subindex)/value=float bits.
	//
	// Flag was asserted to be ZERO, and is on every entry of all but 2 captured spawns. Those two carry a 1 on their
	// LAST entry, and that entry's Value is a plain integer (300, 400) where a flag-0 entry's Value is float bits - so
	// it discriminates how Value is read, rather than being padding. Carried, not interpreted.
	public readonly record struct ScaffoldEntry(uint Index, ushort Tag, uint Value, uint Flag = 0);

	// One 5-byte ID-list group [u8 rank][u32 id]; the id 0x3F1 terminates the list on the wire. Ids USUALLY ascend and
	// were long asserted to - 17 captured spawns break the order (a re-trained/re-ordered list), so the order is
	// carried as captured rather than enforced.
	public readonly record struct ScaffoldIdEntry(byte Rank, uint Id);

	// One 13-byte record closing the ID list: [u32 0xC350][u32 Id][u32 0][u8 Kind]. Kind is 0x01 or 0x04 in the
	// captures. The leading 0xC350 and the zero word are fixed and asserted on parse rather than carried.
	public readonly record struct ScaffoldTailRef(uint Id, byte Kind);

	// One 8-byte ending pair [u32 id][s32 value] (ids are decades 10..110).
	public readonly record struct ScaffoldPair(uint Id, int Value);
}
