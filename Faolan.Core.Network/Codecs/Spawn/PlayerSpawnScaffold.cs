using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

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
		// The player's own values for the shared header's last two words. Constant across every captured player spawn,
		// which is why they used to be bare literals in the codec - but constant for the PLAYER, not for the protocol:
		// an NPC carries 5 and 1/2/3 in the same two slots (SpawnPostIirHeader).
		public const uint PostIirFirstRecordLen = 4;
		public const uint PostIirWord2 = 2;

		// The post-IIR stream MINUS its entry slot: the header, the first record, the active buffs and the three
		// closing pair lists. Shared with the NPC family verbatim (SpawnPostIirCommon) - the entry table and ID list
		// below are the only part of that stream this codec reads for itself.
		public SpawnPostIirCommon Common { get; set; } = new()
		{
			Header = new SpawnPostIirHeader
			{
				FirstRecordLen = PostIirFirstRecordLen,
				Word2 = PostIirWord2
			}
		};

		public List<ScaffoldEntry> Entries { get; } = [];

		public List<ScaffoldIdEntry> IdList { get; } = [];

		// The three closing pair lists, the character's active buffs and the header's head-word list all live on
		// Common - they are not player fields, and the NPC stream carries every one of them. Kept reachable here
		// because that is how the rest of the codebase (the faction rows, the row builders, the tests) names them.
		public List<ScaffoldPair> EndPairs => Common.EndPairs;
		public List<ScaffoldPair> EndPairs2 => Common.EndPairs2;
		public List<ScaffoldPair> EndPairs3 => Common.EndPairs3;
		public List<uint> HeadWords => Common.Header.HeadWords;
		public List<ScaffoldBuff> BuffRecords => Common.BuffRecords;

		// The embedded character message - identity, appearance, equipment. TYPED, not bytes: PlayerSpawnReader
		// already decodes it to find the message at all, so holding the bytes beside it meant carrying the same
		// thing twice and re-parsing at every use. Its re-encode is byte-identical on every captured spawn
		// (ProtobufCorpusRoundTripTests), and the whole-body regeneration gate covers it end to end.
		public ProtoCoreCharacterIIR Character { get; set; } = new();

		// The pre-IIR frame between the transform and the CharacterIIR length prefix: the client's own
		// SimpleCharFullUpdateIIR scaffold (the same one NpcSpawnPreIir models) plus the embedded CoreCharacter's
		// version byte + IirHeader, its sublist being the character's saved FEAT SPECS (#466 / #474).
		//
		// TYPED, not bytes. It was held as a byte[] long after PlayerSpawnPreFrameCodec could parse and rebuild
		// every captured frame byte-exact, which meant PlayerSpawnScaffoldRows serialised a frame it had just built
		// and five call sites parsed it straight back - a round trip through the wire format, in memory, to reach
		// fields that were already there.
		public PlayerSpawnPreFrame Frame { get; set; } = new();

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
}
