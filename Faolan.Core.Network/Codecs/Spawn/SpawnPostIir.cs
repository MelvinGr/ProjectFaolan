using System.Collections.Generic;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// The FA015807 post-IIR stream - the bytes after the embedded CoreCharacterIIR (Gitea #475). PLAYER and NPC are
	// the SAME grammar, entry slot included (#466 - the slot was long modelled as two family shapes; it is one):
	//
	//   [u32 headWordCount][headWordCount x u32][u32 firstRecordLen][u32 word2]   the header
	//   [firstRecordLen-4 move-task slot][u32 list frame]                         the FIRST RECORD, ending in the
	//                                                                             buff list's count frame
	//   [count x ScaffoldBuffGroup]                                               the active buffs, grouped by spell
	//   [SpawnEntrySlot]                                                          appearance + skills + pets
	//   [u32 c1][c1 x 8][u32 c2][c2 x 8]                                          two PLAIN-counted pair lists
	//   [u32 list frame][n x 8]                                                   the count-framed third
	//
	// ⚠️ THE BUFF COUNT IS READ, NEVER SEARCHED. The last four bytes of the first record are that list's framing word
	// on all 11,632 captured streams, without exception. This was not obvious from the player side alone: a player's
	// first record IS the frame word (length 4, no slot), so the frame reads as "the record", while an NPC's carries
	// a prefix in front of it - one 0x00 byte on a fresh spawn, up to 1,273 bytes of accrued runtime state
	// mid-session. That prefix is the entity's current MOVE TASK (#477). Gate: SpawnPostIirGrammarTests.
	public sealed record SpawnPostIir
	{
		// The stream's own leading plain-counted u32 list. Empty on every NPC and on all but a handful of players
		// (those carry a single word, 60), which is exactly why it read as a constant 0 for so long.
		public List<uint> HeadWords { get; } = [];

		// The length in bytes of the FIRST record. 4 on a player - the buff list's count word alone, which stays 4
		// whether the character has 0 or 3 buffs because the buff records are that list's ELEMENTS - and 5 on an
		// NPC's minimal form, whose first record carries the empty move-task slot in front of it. CARRIED on read
		// and DERIVED on write from what the slot turned out to be: two sources of truth for one width is how the
		// fixed-width readers went wrong.
		public uint FirstRecordLen { get; set; } = 4;

		// The third header word - and it is the entity's MOVEMENT MODE, the same value the embedded
		// ProtoCoreCharacterIIR carries in its own `movement_mode` field (21). Sent TWICE in one spawn body, in two
		// unrelated byte regions: this u32 in the post-IIR header, and a protobuf varint inside the character message
		// ahead of it (Gitea #605, `--report field-predictors`).
		//
		// The evidence is a whole-corpus cross-tab, and it is as clean as this kind of claim gets: over 5,739 captured
		// NPC spawns the two agree on 5,738 - the single exception is a Rocknose part-way through a path plan - and
		// over 209 captured PLAYER spawns they agree on all 209 (both 2). That second family is what makes it a
		// protocol fact rather than an NPC convention: the player scaffold is a different grammar and was never part
		// of the measurement that found it.
		//
		// ⚠️ It was long recorded here as "NOT a count of anything, type-stable per NPC, meaning unknown". The first
		// half was right and is what made the rest findable; the SECOND half was not. It is not type-stable: it is
		// rolled per spawn event, 61.9% stable across templates even when restricted to freshly-spawned NPCs with no
		// move task and no buffs. The old claim ("no type is ever seen with two") was unfalsifiable by construction -
		// the importer scan that produced it stopped at the FIRST sighting of each type, so a second value could not
		// have been observed. See [[first_record_wins_silently_truncates_repeated_wire_records]].
		//
		// What the three values MEAN is not settled and is deliberately not guessed at here. What the corpus does say
		// is that 1 goes with MOTION: of the 562 spawns carrying an active move-task plan, 559 are 1, where a spawn
		// with an empty slot is 2 on 4,064 of 5,168. Value 3 is 14 sightings, every one of them a Flesheater with an
		// empty slot.
		public uint MovementMode { get; set; } = 2;

		// Whether the first record carries the move-task slot at all: true on every NPC stream, false on every
		// player one. Kept distinct from `MoveTask == null`, because a fresh NPC has the SLOT with no task in it and
		// that is one byte of wire difference.
		public bool HasMoveTaskSlot { get; set; }

		// The first record's MOVE TASK (#477). It is a MoveTask, and we have parsed that grammar since #53: all
		// 1,679 captured prefixes are consumed to their exact last byte by the MoveTask reader, in the same five
		// modes the MoveTaskIIR packet uses (path x1,636, approach-facing x22, approach-target x18, ...).
		public MoveTaskNode? MoveTask { get; set; }

		// The active buffs, GROUPED BY SPELL - the client's own carve (#658): FUN_0145b8a0 reads the outer frame
		// and one u32 spell id per group, FUN_0145c0b0 the inner frame, FUN_007ed9b0 each record. The list was long
		// modelled FLAT as 60-byte records whose word 1 was a "Kind = ForCount(1) on all 1,471" - that word is the
		// INNER frame, constant only because no captured entity carries two records of one spell. The flat carve
		// would have MISPARSED the first M>=2 stream; this one reads it.
		public List<ScaffoldBuffGroup> BuffRecords { get; } = [];

		// The appearance/skills/pets slot - one grammar for both families; an NPC's skill and pet lists are empty.
		public SpawnEntrySlot Slot { get; set; } = new SpawnEntrySlot();

		// The first of the three closing lists IS the faction-reputation block (#315); see ScaffoldPair.
		public List<ScaffoldPair> EndPairs { get; } = [];
		public List<ScaffoldPair> EndPairs2 { get; } = [];

		// The third, and the stream's last field. ⭐ It is a STAT list, not a third helping of the faction block -
		// which is what the asymmetric framing was saying (#466). The two above are framed by a PLAIN count and
		// carry FACTION ids in the decades; this one is framed by a ListFrame word and carries wire STAT ids, every
		// one of which resolves in the client's own name table:
		//
		//     20      BCCState                    x8   value 1          NPC spawns
		//     975     DamageAbsorb                x39  value 0          both families
		//     1351    NaturalEnergyRegeneration   x9   5625 / 7368      PLAYER spawns only
		//     1000028 Energy                      x9   3222/9000/14000  PLAYER spawns only
		//     1000029 MaxEnergy                   x9   9000 / 14000     PLAYER spawns only
		//
		// The energy triple lands on the same 9 spawns with Energy <= MaxEnergy on every one - a pool, its
		// denominator and its regen rate, pushed together. ⚠️ So `MaxEnergy` IS on the wire; the note that it
		// "appears ZERO times in 120 captures" was reading this list as a leaked scaffold region.
		// [V3: SpawnPostIirGrammarTests.TheThirdPairList_IsAStatList_NotAThirdFactionList]
		//
		// It was read as a closing 0x3F1 constant for years because ForCount(0) IS 0x3F1 and it is empty on all but
		// 56 captured spawns.
		public List<ScaffoldPair> EndPairs3 { get; } = [];
	}

	// The ENTRY SLOT - ONE region, ONE grammar for both families. It was long modelled as two family-specific
	// shapes (a player "entry table + id list" vs an NPC "morph table + two trailing constants"); the client reads
	// both through the same CoreCharacterFullUpdateIIR::ReadSubClass (FUN_0145ac80), and its carve sat 8 bytes OFF
	// the old player reading (#466):
	//
	//   [u32 count][count x appearance entry]          FUN_010087b0 - each entry -> MorphValues_c::SetMorphValue
	//   [u32 ListFrame][n x [u32 id][u8 rank]]         FUN_0079cfd0 - the SKILLS, stored map[id] = rank
	//   [u32 ListFrame][m x 13-byte pet record]        FUN_0145ba10 - the PETS (ScaffoldPetRef)
	//
	// An NPC is simply a stream whose skill and pet lists are EMPTY - their two ForCount(0) frame words are what
	// used to be carried as the "EntryTableX + EntryTableMarker" trailing constants. The old player carve hid the
	// same two words inside invented fields; see AppearanceEntry for the full unwind.
	public sealed record SpawnEntrySlot
	{
		// The appearance/morph table - empty on animals and on the create-editor preview.
		public List<AppearanceEntry> Appearance { get; } = [];

		// The skill list. EMPTY on every NPC; every captured player spawn with an appearance carries its archetype
		// ability first (100/200/300/400 = archetype x 100, rank 0 on all 296 - derived per character, like the
		// class-progression entry, see PlayerSpawnScaffoldRows).
		public List<ScaffoldSkill> Skills { get; } = [];

		// The pet list that closes the slot. See ScaffoldPetRef.
		public List<ScaffoldPetRef> Pets { get; } = [];
	}

	// One element of the slot's appearance table: [u32 key][f32 value][u32 word2][u16 len][len x Latin1]. The
	// client hands (key, value, word2) plus the name string to MorphValues_c::SetMorphValue (FUN_01008070, log
	// channel GameCode/MorphValues), which upserts a 76-byte morph-value entry keyed on `key` and zeroes rival
	// entries of the same EXCLUSIVE appearance category (the DAT_01f268b4 category set). The NAME is the editor
	// spelling ("*hairmesh_208094"); it is EMPTY on every captured element of BOTH families, where the key carries
	// the identity - but the client can read a named one, so the u16 length is real grammar, not padding. On a
	// weighted resource entry Value is the registry weight (1.0); on a morph slider entry, the slider float. Word2
	// is a per-character slot ordinal (the NPC texscript case is argued on NpcSpawnMorph.Word2).
	//
	// ⭐⭐ This ONE element shape is what unwound the player entry table (#466). The player slot was long carved as
	// [u32 count][u32 X][f32 1.0][count x [u32 idx][u32 flag][u16 tag][u32 value]] + an id list closed by a
	// "terminator" - a reading that round-trips byte-exact (it covers the same bytes) and is 8 bytes OFF the
	// client's. Every invented field was this element seen through a shifted frame:
	//
	//   - "EntryTableX" + the asserted 1.0f = the FIRST element's (key, value) - a real appearance choice, usually
	//     the always-sent face-paint texscript (X = 0xB7 = 183), which the old carve then LOST on regeneration;
	//   - each entry's (tag, value) = one element's (key, value); its "idx" = the NEXT element's word2;
	//   - the "closer" record (tag = k x 0x3F1, value = a round hundred) = the SKILL list's ListFrame word plus
	//     the first skill's id (the archetype ability);
	//   - the "flag-1 entry" (7 distinct tags matching nothing we hold) = that same frame word once the character
	//     has >= 65 skills and (skills + 1) x 0x3F1 overflows 16 bits: all seven tags satisfy
	//     (0x10000 | tag) % 0x3F1 == 0, framing 71..120 skills - and the u16-truncating writer would have CRASHED
	//     any generated 65+-skill character at world entry (#250's failure mode);
	//   - the id-list "terminator rank" = the LAST skill's rank (nonzero on 28 captured spawns).
	//
	// [V3: SpawnPostIirGrammarTests.EveryPlayerSlot_OpensItsSkillListWithTheArchetypeAbility]
	public sealed class AppearanceEntry
	{
		public uint MorphId { get; set; }
		public float Value { get; set; }
		public uint Word2 { get; set; }
		public string Text { get; set; } = "";
	}

	// One 5-byte element [u32 id][u8 rank] of the slot's skill list (FUN_0079cfd0 reads the u32 THEN the byte and
	// stores map[id] = rank client-side). Ids ascend on all but a few captured spawns (re-trained/re-ordered
	// lists), so order is carried, not enforced.
	public readonly record struct ScaffoldSkill(uint Id, byte Rank);

	/// <summary>
	///     One 13-byte record closing the ID list - <c>[u32 50000][u32 PetInstance][u32 SlotSelector][u8 Kind]</c>,
	///     and it is one of the character's PETS (#466).
	/// </summary>
	/// <remarks>
	///     <para>
	///         ⭐ Named from the client, end to end. The reader is <c>FUN_0145ba10</c> (ListFrame'd, count capped at
	///         0x7531) over the element reader <c>FUN_014578e0</c>, which fills a 16-byte struct. The spawn APPLY
	///         (<c>FUN_01458d40</c>) then walks that vector with stride 0x10 and hands each record to
	///         <c>FUN_0081b960</c>, which allocates a <b><c>BasePet_c</c></b> (RTTI <c>.?AVBasePet_c@@</c>, 0x28
	///         bytes) and registers it through <c>FUN_0081b670</c> - whose own assert cites
	///         <b><c>CoreCharacter\Pets\petholder.cpp</c></b> and its parameter <c>i_pcNewPet</c>, and which logs
	///         <c>"PetHolder_c::RegisterPet: Pet registered (instance %i)!"</c> on channel <c>GameCode/Pets</c>.
	///     </para>
	///     <para>
	///         The fields land on <c>BasePet_c</c> as: <c>Kind</c> → <c>+0x0C</c>, the leading 50000 →
	///         <c>+0x18</c>, <c>PetInstance</c> → <c>+0x1C</c>, <c>SlotSelector</c> → <c>+0x20</c>. 50000 is
	///         <see cref="Constants.EntityClass.Character" />, and the consumer special-cases exactly that value to
	///         resolve the referent - so the pair is the ordinary <c>(class, instance)</c> entity reference.
	///         <c>PetInstance</c> is what the log line prints and what <c>RegisterPet</c> de-dupes on.
	///     </para>
	///     <para>
	///         ⚠️ <b><c>SlotSelector</c> was asserted to be ZERO and is not a constant.</b> <c>RegisterPet</c> tests
	///         <c>pet->+0x20 == 1</c> and puts that pet in the holder's SINGLE slot at <c>+0x4c</c>, releasing
	///         whatever was there; anything else joins the general list. It is 0 on every captured spawn because no
	///         captured character had a primary pet out - so a player who did would have failed the parse. Carried
	///         now, not asserted.
	///     </para>
	///     <para>
	///         <c>Kind</c> is 0x01 or 0x04 in the captures and is <c>BasePet_c</c>'s own field at <c>+0x0C</c>;
	///         nothing has named it yet.
	///     </para>
	/// </remarks>
	public readonly record struct ScaffoldPetRef(uint PetInstance, byte Kind, uint SlotSelector = 0);

	// One 8-byte pair [u32 id][s32 value] in the three lists that CLOSE a post-IIR stream. ⚠️ The 8-byte shape is
	// shared by TWO unrelated families, which is why the framing differs: lists 1 and 2 are the FACTION-REPUTATION
	// block (Id = faction id in the decades 10..330, Value = signed standing +/-50000, shown in the client's
	// Factions window - see CharacterScaffoldPair / Gitea #315), and list 3 is a STAT list (Id = a wire stat id,
	// Value = its value - see EndPairs3, Gitea #466).
	public readonly record struct ScaffoldPair(uint Id, int Value);

	// One SPELL's group in the buff list: the spell id, then an inner ListFrame'd list of records. Decoded over
	// all 1,471 captured records (#475 flat, #658 re-carved to the client's own grammar - see BuffRecords).
	//
	// NOT a player record: it lives in the shared part of the stream and NPCs carry it too. Nine distinct spells
	// turn up on NPC spawns, all rows in our own Spells table - Stagger, Hearty Meal, Shrink, Frozen Hatred, two
	// necromancer PET summons - which is corroboration from a direction the walk knows nothing about.
	public sealed class ScaffoldBuffGroup
	{
		// THE find of #475: the distinct values are rows in our own Spells catalog, and they read as a buff bar -
		// "Hearty Meal", "Focused Mind", "Refresh Strength", "Death Penalty 1", "Spirit of the Bear (Rank 1)".
		public uint SpellId { get; set; }

		// ForCount(1) on every captured group - one record per spell - which is why the flat carve's "Kind" word
		// read as a constant.
		public List<ScaffoldBuff> Records { get; } = [];
	}

	// One 52-byte buff record: a leading u32 the client READS AND DISCARDS (FUN_0145c0b0), then twelve u32 the
	// client stores onto a BuffInfo_c (200 bytes, ctor FUN_00849770) at the offsets below (FUN_007ed9b0) - the SAME
	// object a MagicBuffIIR apply fills through CoreMagicHolder::UpdateBuff, whose own stores anchor four of them
	// (#658). Field names predate the offset mapping and are corpus-derived where the client is silent:
	//
	//   SourceHandle    the DISCARDED word - 0 on nearly all; the exceptions carry a value in the same 0x131xxxx
	//                   range as TargetHandle
	//   Stamp           -> +0x14   large values (874724..98289218), unrelated to the timer pair. Unknown
	//   TimerTotal      -> +0x18   ⭐ the apply path COMPUTES this slot itself: duration x stat 0x1D0
	//                   DurationModifier x a constant (UpdateBuff @007eeb5e), so the scaffold carries the same
	//                   scaled total precomputed. TimerRemaining <= TimerTotal on ALL 941 timed records
	//   TimerRemaining  -> +0x1c   the apply path stores its unscaled duration here
	//   Word6           -> +0x20   zero on every record. Carried rather than asserted
	//   CasterLevel     -> +0x24   ⚠️ on the APPLY path this slot receives the POINTER to the 7-word descriptor
	//                   (UpdateBuff arg 3, stored raw @007eeb6d) - so the two paths do NOT store the same kind of
	//                   value here, and the corpus reading (0..82, plausibly a level; 82 is above the cap, so no
	//                   bound is asserted) names only the scaffold's half
	//   TargetClass     -> +0x28   EntityClass.Character on all 941, and
	//   TargetHandle    -> +0x2c   the spawning character's own handle on nearly all: who the buff is ON
	//   Trailer[0..4]   -> +0xa8 +0xac +0xb8 +0xbc +0xc0   zero on every record. On the apply path +0xa8 gets the
	//                   constant 0, +0xac the Duration (gated > 0) and +0xc0 the CasterID - so a scaffold record
	//                   arrives with no duration in the slot the apply gates on, carrying its time in the pair above
	//
	// ⚠️ This is how retail tells an OBSERVER about buffs on an entity streaming into view (#677) - it ATTACHES
	// them to the spawn (1,064 of 17,717 parsed spawn bodies carry at least one), so there is no MagicBuffIIR
	// fan-out to design, and TimerRemaining states the remaining time directly.
	// [V3: SpawnBuffRecordTests.RemainingIsNotJustTheTotalAgain]
	public sealed class ScaffoldBuff
	{
		public uint SourceHandle { get; set; }
		public uint Stamp { get; set; }
		public uint TimerTotal { get; set; }
		public uint TimerRemaining { get; set; }
		public uint Word6 { get; set; }
		public uint CasterLevel { get; set; }
		public uint TargetClass { get; set; } = EntityClass.Character;
		public uint TargetHandle { get; set; }
		public uint[] Trailer { get; set; } = new uint[5];
	}
}
