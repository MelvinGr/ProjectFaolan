using System.Collections.Generic;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Spawn
{
	/// <summary>
	///     The spawn post-IIR stream - the bytes after the embedded CoreCharacterIIR. Player and NPC are the same grammar,
	///     entry slot included:
	///     <code>
	///     [u32 headWordCount][headWordCount x u32][u32 firstRecordLen][u32 movementMode]   the header
	///     [firstRecordLen-4 move-task slot][u32 list frame]   the first record, ending in the buff list's count frame
	///     [count x ScaffoldBuffGroup]                         the active buffs, grouped by spell
	///     [SpawnEntrySlot]                                    appearance, skills and pets
	///     [u32 c1][c1 x 8][u32 c2][c2 x 8]                    two plain-counted pair lists
	///     [u32 list frame][n x 8]                             the count-framed third
	///     </code>
	/// </summary>
	/// <remarks>
	///     The buff count is read, never searched: the last four bytes of the first record are that list's framing word on
	///     all 11,632 captured streams. That is not obvious from the player side alone - a player's first record is the
	///     frame word, four bytes with no slot, so the frame reads as the record, while an NPC's carries a prefix in front
	///     of it: one zero byte on a fresh spawn, up to 1,273 bytes of accrued runtime state mid-session. That prefix is the
	///     entity's current move task. Gated by SpawnPostIirGrammarTests.
	/// </remarks>
	public sealed record SpawnPostIir
	{
		/// <summary>
		///     The stream's own leading plain-counted u32 list. Empty on every NPC and on all but a handful of players,
		///     which carry a single word, 60 - which is what makes its count word look like a constant zero.
		/// </summary>
		public List<uint> HeadWords { get; } = [];

		/// <summary>
		///     The length in bytes of the first record: 4 on a player - the buff list's count word alone, which stays 4
		///     whether the character has none or three buffs, because the buff records are that list's elements - and 5 on an
		///     NPC's minimal form, whose first record carries the empty move-task slot in front of it. It is carried on read
		///     and derived on write from what the slot turned out to be, since two sources of truth for one width is how a
		///     fixed-width reader goes wrong.
		/// </summary>
		public uint FirstRecordLen { get; set; } = 4;

		/// <summary>
		///     The third header word: the entity's movement mode, the same value the embedded ProtoCoreCharacterIIR carries
		///     in its own <c>movement_mode</c> field. It is sent twice in one spawn body, in two unrelated byte regions -
		///     this u32 in the post-IIR header, and a protobuf varint inside the character message ahead of it.
		/// </summary>
		/// <remarks>
		///     The evidence is a whole-corpus cross-tab: over 5,739 captured NPC spawns the two agree on 5,738, the single
		///     exception being a creature part-way through a path plan, and over 209 captured player spawns they agree on
		///     all 209. That second family is what makes it a protocol fact rather than an NPC convention, the player
		///     scaffold being a different grammar that was never part of the measurement.
		///     <para>
		///         It is not type-stable and must not be treated as type identity: it is rolled per spawn event, 61.9% stable
		///         across templates even when restricted to freshly spawned NPCs with no move task and no buffs.
		///     </para>
		///     <para>
		///         What the three values mean is not settled. What the corpus does say is that 1 goes with motion: of the 562
		///         spawns carrying an active move-task plan, 559 are 1, while a spawn with an empty slot is 2 on 4,064 of
		///         5,168. Value 3 has 14 sightings, every one of them the same creature with an empty slot.
		///     </para>
		/// </remarks>
		public uint MovementMode { get; set; } = 2;

		/// <summary>
		///     Whether the first record carries the move-task slot at all: true on every NPC stream, false on every player
		///     one. It is kept distinct from a null <see cref="MoveTask" />, because a fresh NPC has the slot with no task in
		///     it, and that is one byte of wire difference.
		/// </summary>
		public bool HasMoveTaskSlot { get; set; }

		/// <summary>
		///     The first record's move task, in the same grammar the MoveTaskIIR packet uses: all 1,679 captured prefixes are
		///     consumed to their exact last byte by the MoveTask reader, across the same five modes (path 1,636 times,
		///     approach-facing 22, approach-target 18, and so on).
		/// </summary>
		public MoveTaskNode? MoveTask { get; set; }

		/// <summary>
		///     The active buffs, grouped by spell, which is the client's own carve: FUN_0145b8a0 reads the outer frame and
		///     one u32 spell id per group, FUN_0145c0b0 the inner frame and FUN_007ed9b0 each record. A flat model of 60-byte
		///     records reads the inner frame as a per-record constant, which holds only because no captured entity carries
		///     two records of one spell, and mis-parses the first stream that does.
		/// </summary>
		public List<ScaffoldBuffGroup> BuffRecords { get; } = [];

		/// <summary>
		///     The appearance, skills and pets slot - one grammar for both families; an NPC's skill and pet lists are empty.
		/// </summary>
		public SpawnEntrySlot Slot { get; set; } = new SpawnEntrySlot();

		/// <summary>
		///     The first two of the three closing lists are the faction-reputation block; see <see cref="ScaffoldPair" />.
		/// </summary>
		public List<ScaffoldPair> EndPairs { get; } = [];
		public List<ScaffoldPair> EndPairs2 { get; } = [];

		/// <summary>
		///     The third closing list, and the stream's last field. It is a stat list rather than a third helping of the
		///     faction block, which is what the asymmetric framing says: the two above are framed by a plain count and carry
		///     faction ids in the decades, while this one is framed by a <see cref="ListFrame" /> word and carries wire stat
		///     ids, every one of which resolves in the client's own name table.
		/// </summary>
		/// <remarks>
		///     <code>
		///     20      BCCState                    x8   value 1          NPC spawns
		///     975     DamageAbsorb                x39  value 0          both families
		///     1351    NaturalEnergyRegeneration   x9   5625 / 7368      player spawns only
		///     1000028 Energy                      x9   3222/9000/14000  player spawns only
		///     1000029 MaxEnergy                   x9   9000 / 14000     player spawns only
		///     </code>
		///     The energy triple lands on the same 9 spawns, with Energy no greater than MaxEnergy on every one: a pool, its
		///     denominator and its regeneration rate, pushed together. So MaxEnergy is on the wire, in this list.
		///     [V3: SpawnPostIirGrammarTests.TheThirdPairList_IsAStatList_NotAThirdFactionList]
		///     <para>
		///         It is empty on all but 56 captured spawns, and an empty list frames as the empty-list word, which is what
		///         makes the closing word look like a constant.
		///     </para>
		/// </remarks>
		public List<ScaffoldPair> EndPairs3 { get; } = [];
	}

	/// <summary>
	///     The entry slot: one region, one grammar for both families, which the client reads through the same
	///     CoreCharacterFullUpdateIIR::ReadSubClass (FUN_0145ac80).
	///     <code>
	///     [u32 count][count x appearance entry]    FUN_010087b0 - each entry goes to MorphValues_c::SetMorphValue
	///     [u32 ListFrame][n x [u32 id][u8 rank]]   FUN_0079cfd0 - the skills, stored as map[id] = rank
	///     [u32 ListFrame][m x 13-byte pet record]  FUN_0145ba10 - the pets (ScaffoldPetRef)
	///     </code>
	/// </summary>
	/// <remarks>
	///     An NPC is simply a stream whose skill and pet lists are empty; their two empty-list frame words are easily
	///     mistaken for trailing constants.
	/// </remarks>
	public sealed record SpawnEntrySlot
	{
		/// <summary>The appearance and morph table - empty on animals and on the create-editor preview.</summary>
		public List<AppearanceEntry> Appearance { get; } = [];

		/// <summary>
		///     The skill list. Empty on every NPC; every captured player spawn with an appearance carries its archetype
		///     ability first (100, 200, 300 or 400, the archetype times 100, at rank 0 on all 296), derived per character
		///     like the class-progression entry - see <see cref="PlayerSpawnScaffoldRows" />.
		/// </summary>
		public List<ScaffoldSkill> Skills { get; } = [];

		/// <summary>The pet list that closes the slot. See <see cref="ScaffoldPetRef" />.</summary>
		public List<ScaffoldPetRef> Pets { get; } = [];
	}

	/// <summary>
	///     One element of the slot's appearance table: <c>[u32 key][f32 value][u32 word2][u16 len][len x Latin1]</c>.
	/// </summary>
	/// <remarks>
	///     The client hands (key, value, word2) plus the name string to MorphValues_c::SetMorphValue (FUN_01008070, log
	///     channel GameCode/MorphValues), which upserts a 76-byte morph-value entry keyed on the key and zeroes rival
	///     entries of the same exclusive appearance category.
	///     <para>
	///         The name is the editor spelling, such as "*hairmesh_208094". It is empty on every captured element of both
	///         families, the key carrying the identity, but the client can read a named one, so the u16 length is real
	///         grammar rather than padding. On a weighted resource entry the value is the registry weight, 1.0; on a morph
	///         slider entry it is the slider float. Word2 is a per-character slot ordinal - the NPC texscript case is argued
	///         on NpcSpawnMorph.Word2.
	///     </para>
	///     <para>
	///         This one element shape is what the whole player entry table is made of. A carve shifted eight bytes from the
	///         client's still round-trips byte-exact, because it covers the same bytes, while inventing fields out of the
	///         element's parts, the skill list's frame word and the archetype ability. Any reading of this region has to be
	///         checked against the client's, not against a round-trip.
	///         [V3: SpawnPostIirGrammarTests.EveryPlayerSlot_OpensItsSkillListWithTheArchetypeAbility]
	///     </para>
	/// </remarks>
	public sealed class AppearanceEntry
	{
		public uint MorphId { get; set; }
		public float Value { get; set; }
		public uint Word2 { get; set; }
		public string Text { get; set; } = "";
	}

	/// <summary>
	///     One 5-byte element <c>[u32 id][u8 rank]</c> of the slot's skill list; FUN_0079cfd0 reads the u32 then the byte
	///     and stores map[id] = rank client-side. Ids ascend on all but a few captured spawns - re-trained or re-ordered
	///     lists - so order is carried rather than enforced.
	/// </summary>
	public readonly record struct ScaffoldSkill(uint Id, byte Rank);

	/// <summary>
	///     One 13-byte record closing the id list - <c>[u32 50000][u32 PetInstance][u32 SlotSelector][u8 Kind]</c> - and
	///     one of the character's pets.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Named from the client, end to end. The reader is <c>FUN_0145ba10</c> (count-framed, capped at
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
	///         <c>SlotSelector</c> is not a constant and must not be asserted. <c>RegisterPet</c> tests
	///         <c>pet-&gt;+0x20 == 1</c> and puts that pet in the holder's single slot at <c>+0x4c</c>, releasing
	///         whatever was there; anything else joins the general list. It is 0 on every captured spawn only because
	///         no captured character had a primary pet out, so a player who did would fail the parse.
	///     </para>
	///     <para>
	///         <c>Kind</c> is 0x01 or 0x04 in the captures and is <c>BasePet_c</c>'s own field at <c>+0x0C</c>;
	///         nothing has named it yet.
	///     </para>
	/// </remarks>
	public readonly record struct ScaffoldPetRef(uint PetInstance, byte Kind, uint SlotSelector = 0);

	/// <summary>
	///     One 8-byte pair <c>[u32 id][s32 value]</c> in the three lists that close a post-IIR stream.
	/// </summary>
	/// <remarks>
	///     The 8-byte shape is shared by two unrelated families, which is why the framing differs. Lists 1 and 2 are the
	///     faction-reputation block - the id a faction id in the decades 10 to 330, the value a signed standing within
	///     plus or minus 50000, shown in the client's Factions window; see CharacterScaffoldPair. List 3 is a stat list,
	///     the id a wire stat id and the value its value; see <see cref="SpawnPostIir.EndPairs3" />.
	/// </remarks>
	public readonly record struct ScaffoldPair(uint Id, int Value);

	/// <summary>
	///     One spell's group in the buff list: the spell id, then an inner count-framed list of records. Decoded over all
	///     1,471 captured records, in the client's own grammar.
	/// </summary>
	/// <remarks>
	///     It is not a player record: it lives in the shared part of the stream and NPCs carry it too. Nine distinct spells
	///     turn up on NPC spawns, all rows in our own Spells table - Stagger, Hearty Meal, Shrink, Frozen Hatred and two
	///     necromancer pet summons - which corroborates the carve from a direction the walk knows nothing about.
	/// </remarks>
	public sealed class ScaffoldBuffGroup
	{
		/// <summary>
		///     The spell the group belongs to. The distinct values are rows in our own Spells catalog and read as a buff bar:
		///     "Hearty Meal", "Focused Mind", "Refresh Strength", "Death Penalty 1", "Spirit of the Bear (Rank 1)".
		/// </summary>
		public uint SpellId { get; set; }

		/// <summary>
		///     The group's records. Every captured group holds exactly one, which is what makes its inner frame word look
		///     like a constant.
		/// </summary>
		public List<ScaffoldBuff> Records { get; } = [];
	}

	/// <summary>
	///     One 52-byte buff record: a leading u32 the client reads and discards (FUN_0145c0b0), then twelve u32 it stores
	///     onto a BuffInfo_c (200 bytes, constructor FUN_00849770) at the offsets below (FUN_007ed9b0) - the same object a
	///     MagicBuffIIR apply fills through CoreMagicHolder::UpdateBuff, whose own stores anchor four of them.
	/// </summary>
	/// <remarks>
	///     Field names are corpus-derived where the client is silent.
	///     <code>
	///     SourceHandle    the discarded word - 0 on nearly all; the exceptions carry a value in the same range as
	///                     TargetHandle
	///     Stamp           +0x14  large values (874724..98289218), unrelated to the timer pair. Unknown
	///     TimerTotal      +0x18  the apply path computes this slot itself - duration x stat 0x1D0 DurationModifier x a
	///                     constant (UpdateBuff @007eeb5e) - so the scaffold carries the same scaled total
	///                     precomputed. TimerRemaining never exceeds it across all 941 timed records
	///     TimerRemaining  +0x1c  the apply path stores its unscaled duration here
	///     Word6           +0x20  zero on every record; carried rather than asserted
	///     CasterLevel     +0x24  on the apply path this slot receives the pointer to the 7-word descriptor
	///                     (UpdateBuff arg 3, stored raw @007eeb6d), so the two paths do not store the same kind of
	///                     value here; the corpus reading (0..82, plausibly a level, 82 being above the cap so no
	///                     bound is asserted) names only the scaffold's half
	///     TargetClass     +0x28  EntityClass.Character on all 941 records, and
	///     TargetHandle    +0x2c  the spawning character's own handle on nearly all: who the buff is on
	///     Trailer[0..4]   +0xa8 +0xac +0xb8 +0xbc +0xc0  zero on every record. On the apply path +0xa8 gets the
	///                     constant 0, +0xac the duration (gated above 0) and +0xc0 the caster id, so a scaffold
	///                     record arrives with no duration in the slot the apply gates on, carrying its time in the
	///                     timer pair above
	///     </code>
	///     <para>
	///         This is how retail tells an observer about buffs on an entity streaming into view: it attaches them to the
	///         spawn - 1,064 of 17,717 parsed spawn bodies carry at least one - so there is no MagicBuffIIR fan-out to
	///         design, and TimerRemaining states the remaining time directly.
	///         [V3: SpawnBuffRecordTests.RemainingIsNotJustTheTotalAgain]
	///     </para>
	/// </remarks>
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
