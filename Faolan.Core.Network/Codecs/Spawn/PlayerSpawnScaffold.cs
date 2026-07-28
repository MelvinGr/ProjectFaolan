using System.Collections.Generic;
using System.Numerics;
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

		// The character's ACTIVE BUFFS - transient session state, present only on a captured character that had one
		// running. Decoded (#475); see ScaffoldBuff.
		public List<ScaffoldBuff> BuffRecords { get; } = [];

		// The embedded ProtoCoreCharacterIIR bytes (typed round-trip via FromProtobuf/ToProtobufBytes is
		// byte-identical; see PlayerSpawnGenerationTests).
		public byte[] CharacterIir { get; set; } = null!;

		// The pre-IIR frame between the transform and the CharacterIIR length prefix. DECODED - it is the client's
		// SimpleCharFullUpdateIIR scaffold (the same one NpcSpawnPreIir models) plus the embedded CoreCharacter's
		// version byte + IirHeader, and its sublist is the character's saved FEAT SPECS: see PlayerSpawnPreFrame /
		// PlayerSpawnPreFrameCodec, which parse and rebuild every captured frame byte-exact. Held as bytes here
		// because the scaffold round-trips verbatim; PlayerSpawnScaffoldRows.FreshPreFrame builds a fresh one.
		public byte[] PreFrame { get; set; } = null!;

		// Transform component (the 0x2C group): 0x0A vec3 (3 x fixed32), 0x12 quat (4 x fixed32), optional 0x25
		// scale (4 bytes), 0x18 playfield varint. #352 kept these two as raw field payloads on the grounds that
		// typing "would lose the exact captured field-set" - measured over 11,632 captured player spawns, the field
		// set NEVER varies (vec3 is always {0D,15,1D}, quat always {0D,15,1D,25}), and PlayerSpawnBuilder already
		// constructs both through ProtoVec3/ProtoQuat. So there is no captured field-set to lose, and the
		// byte-exact regeneration gate over every captured spawn is what says so rather than this comment.
		public Quaternion Rotation { get; set; }
		public Vector3 Position { get; set; }

		// Field 4 as a fixed32, kept RAW - unlike the two above this one is genuinely unmodelled: it is absent from
		// all 11,632 captured player spawns, so the corpus proves nothing about what it would contain, and
		// ProtoGameCodeDynelIIR declares field 4 as `repeated RunningEffects` (length-delimited, not fixed32). If a
		// 0x25 ever does turn up, one of the two models is wrong and it needs decoding, not typing.
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

	// One 60-byte ACTIVE BUFF record in the post-IIR stream, count-framed. Fifteen u32, decoded over all 95 captured
	// records (Gitea #475) - it was carried as an opaque byte[] described only as "buff/timer state".
	//
	// What the corpus establishes, and what it does not:
	//   SpellId       w0  - THE find: 35 of its 36 distinct values are rows in our own Spells catalog, and they read
	//                       as a buff bar - "Hearty Meal", "Focused Mind", "Refresh Strength", "Death Penalty 1",
	//                       "Spirit of the Bear (Rank 1)". That is what identifies the record.
	//   Kind          w1  - ListFrame.ForCount(1) on all 95. ⚠️ Whether that is a CONSTANT or a real one-element
	//                       list count cannot be decided from this corpus: the value never varies, so the record's
	//                       fixed 60-byte width is equally consistent with "a constant" and with "a one-element list
	//                       whose element is the remaining 52 bytes". That is the 0x7E2 trap's exact signature, so it
	//                       is written as ForCount(1) rather than as a magic number, and CARRIED per record - a
	//                       capture with a count of 2 then round-trips and shows up, instead of being rejected.
	//   SourceHandle  w2  - 0 on 94; the one exception carries a value in the same 0x131xxxx range as TargetHandle,
	//                       so it reads as an entity reference that is simply absent for a self-applied buff.
	//   Stamp         w3  - 37 distinct large values (874724..98289218), unrelated to the timer pair. Unknown.
	//   TimerTotal    w4  } TimerRemaining <= TimerTotal on ALL 95 records, which is what makes them a PAIR rather
	//   TimerRemaining w5 } than two independent numbers. Their unit is not pinned; only the ordering is evidence.
	//   CasterLevel   w7  - 1..80 exactly, the game's level range. #438 independently found the caster's LEVEL in the
	//                       MagicBuff payload, which is the same quantity arriving by another route.
	//   TargetClass   w8  - EntityClass.Character (0xC350) on all 95, and
	//   TargetHandle  w9  - the spawning character's own handle on 94 of 95: the standard [class][handle] entity
	//                       reference, i.e. who the buff is ON.
	//   Word6, Trailer    - zero on every record. Carried rather than asserted, so a non-zero one round-trips
	//                       instead of being silently dropped.
	public sealed class ScaffoldBuff
	{
		public const int ByteCount = 60;
		public const int TrailerWords = 5;

		// See the Kind note above: ForCount(1) on every captured record, and not provably a constant.
		public static readonly uint DefaultKind = ListFrame.ForCount(1);

		public uint SpellId { get; set; }
		public uint Kind { get; set; } = DefaultKind;
		public uint SourceHandle { get; set; }
		public uint Stamp { get; set; }
		public uint TimerTotal { get; set; }
		public uint TimerRemaining { get; set; }
		public uint Word6 { get; set; }
		public uint CasterLevel { get; set; }
		public uint TargetClass { get; set; } = EntityClass.Character;
		public uint TargetHandle { get; set; }
		public uint[] Trailer { get; set; } = new uint[TrailerWords];
	}
}
