using System.Collections.Generic;
using System.Linq;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// The PLAYER FA015807 pre-IIR frame, decoded (Gitea #466). It was carried as an opaque byte[] with seven
	// hand-poked "constant" offsets; it is in fact the SAME scaffold NpcSpawnPreIir already models - the fixed
	// read sequence of SimpleCharFullUpdateIIR_t::ReadSubClass (FUN_01121c00, docs/re/fa015807-spawn-reader.md):
	//
	//   [u8 version 0x05] [6 x u8 + 53 x u32, interleaved per NpcSpawnPreIir.Layout, around one count-framed
	//   SUBLIST] [u8 CoreChar version 0x41] [u32 CoreChar IirHeader]
	//
	// The last two fields are the head of the embedded CoreCharacterFullUpdateIIR, not part of the scaffold: the
	// player parse cuts the frame at the IIR's u32 length prefix, which sits 5 bytes into the CoreChar's 9-byte
	// header ([u8 0x41][u32 iirHeader][u32 msgLen]). That is why the frame appeared to end in a magic
	// "41 00 00 82 08 closer" - it is the version byte 'A' plus the same IirHeader word NpcSpawnBuilder writes
	// from NpcStatCore.IirHeader.
	//
	// The NPC scaffold is only ever seen with an EMPTY sublist, which is why NpcSpawnPreIir holds it as the raw
	// 0x3F1 framing word. The player's is not: it carries exactly one entry, and that entry's own nested
	// count-framed list is the character's UNLOCKED FEAT list. So the three "landmark constants" the frame used
	// to hardcode are two list counts and a reserved gap:
	//   0x07E2 = ListFrame.ForCount(1)  - the sublist has ONE entry
	//   0x03F1 = ListFrame.ForCount(0)  - that entry's feat list is empty on a fresh character
	//
	// Verified over every captured player spawn: 150/150 frames parse to the EXACT byte, in six length variants
	// (228/239/251/259/263/339) that are all this one shape - 228 is the create-editor frame with an empty
	// sublist (= NpcSpawnPreIir.FixedLength + the 5 CoreChar bytes), the rest carry 0/3/5/6/25 feats.
	// (The "339 = geared variant with a 26-slot table" reading in earlier notes was the feat list misread as a
	// table of [00][type][u16] records.)
	public sealed class PlayerSpawnPreFrame
	{
		// Flags index of the frame's archetype byte (Soldier 1 / Priest 2 / Rogue 3 / Mage 4), abs @135 in the
		// spawn body. The only genuinely per-character field in the whole scaffold besides the class byte.
		public const int ArchetypeFlagIndex = 1;

		// Flags index of the class byte (0x12 Barbarian .. 0x2c Demonologist), abs @136.
		public const int ClassFlagIndex = 2;

		// Word index of the category enum - the SAME word NpcSpawnPreIir calls the category (struct 0x1fc), and it
		// carries the same modal value 0x6f on every captured player frame.
		public const int CategoryWordIndex = NpcSpawnPreIir.CategoryWordIndex;

		// Word index of a word that is 0x41700000 (float 15.0) in every captured player frame and 0 on NPCs.
		// Named for what it is - a constant we can reproduce but cannot yet attribute.
		public const int ConstantWordIndex = 19;

		// ...and that value. 0x41700000 is the IEEE754 encoding of 15.0f, so the word is very likely a float, but
		// nothing has been found that varies it, so it is reproduced rather than derived.
		public const uint ConstantWordValue = 0x41700000;

		// Word index of a per-SESSION value: it differs between characters and between sessions of the SAME
		// character (most captured frames carry 6..44; the outliers are round - 1005, 1586, 9000, 11185, 14000).
		// Nothing has been found that predicts it, so the generated frame reproduces the value the captured
		// fresh character carried, which the client accepts.
		public const int SessionWordIndex = 21;

		// ...that value.
		public const uint FreshSessionWordValue = 8;

		// Word index of the vanity SHOWN-slots bitmask (4 bytes big-endian, bit = base equip slot id, SET = that
		// slot's vanity piece is shown; #336). Frame offset 18 - the offset #336 found independently, which is
		// corroboration that this u32 grid is the client's and not a coincidence.
		public const int VanityShownMaskWordIndex = 4;

		// The embedded CoreCharacterFullUpdateIIR's first word, immediately after its 'A' version byte. Every
		// captured player frame carries 0x8208 (one carries 0x820A).
		public const uint DefaultCoreCharIirHeader = 0x8208;

		public byte CoreCharVersion { get; set; } = CoreCharacterVersion;

		public uint CoreCharIirHeader { get; set; } = DefaultCoreCharIirHeader;

		// The 6 single-byte scaffold fields, in wire order (read positions 0, 18, 19, 20, 25, 26).
		public byte[] Flags { get; } = new byte[NpcSpawnPreIir.ByteCount];

		// The count-framed sublist between the 32nd and 33rd scaffold field. Empty on the create-editor frame;
		// exactly one entry on every normal login.
		public List<PlayerSpawnPreFrameSublistEntry> Sublist { get; } = [];

		// The 53 u32 scaffold fields, in wire order, big-endian as read off the wire.
		public uint[] Words { get; } = new uint[NpcSpawnPreIir.WordCount];

		public byte Version { get; set; } = NpcSpawnPreIir.ExpectedVersion;

		// The frame's width on the wire: the fixed scaffold (version byte + 6 u8 + 53 u32 + the sublist's own count
		// word = NpcSpawnPreIir.FixedLength), the 5 embedded CoreCharacter bytes, and each sublist entry's
		// [u32 key][u8 flag][u16 len + name][count word][4 x feat id]. Derived rather than measured, so the six
		// captured length variants (228/239/251/259/263/339) are a consequence of the grammar rather than a list to
		// be matched against.
		public int ByteCount => NpcSpawnPreIir.FixedLength + 5 + Sublist.Sum(e => 4 + 1 + 2 + e.Name.Length + 4 + 4 * e.FeatIds.Count);

		// The embedded CoreCharacterFullUpdateIIR's version byte, 'A'. The client rejects any other value.
		public const byte CoreCharacterVersion = 0x41;
	}

	// One entry of the scaffold's count-framed sublist - a NAMED FEAT SPEC:
	//
	//   [u32 key][u8 flag][u16 len + Latin1 name][count-framed list of u32 feat ids]
	//
	// Every normal login carries exactly one entry with key 0, flag 0 and an EMPTY name - which is why the frame's
	// bytes 119..121 read as "3 reserved zero bytes" and the whole entry read as a lead-in to a bare feat list.
	// Three captured frames (NecroL80, a level-80 necromancer) carry two and four entries, and those are NAMED:
	// "PvE - Lance" (23 feats), "PvP" (27), "PvP - Decap/Protection", "PvP - Decap/Armor", "Hybrid" - the
	// character's saved feat specs, each with its own trained-feat list. Key and flag ascend with the entry index
	// on those frames (0/0, 1/1, ...). The ids are in TRAIN order, not sorted.
	public sealed class PlayerSpawnPreFrameSublistEntry
	{
		public List<uint> FeatIds { get; } = [];
		public byte Flag { get; set; }
		public uint Key { get; set; }
		public string Name { get; set; } = "";
	}
}
