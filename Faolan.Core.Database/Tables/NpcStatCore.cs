using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// The type-level CharacterIIR stat CORE (#244), lifted off the captured `Data` blob into typed columns so an NPC
	// spawn body can be CONSTRUCTED from the DB instead of spliced from a capture. A 1:1 side table keyed to its `NpcTemplate`
	// type (like Character <-> CharacterMaxStats). Property names mirror the protobuf field names in
	// ProtoCoreCharacterIIR's Stats (CharacterStats) / npc_stats (NpcStats) / Bcc (CharacterBcc) sub-messages exactly,
	// so NpcStatCoreColumns maps them by name with no per-field code. Nullable throughout: null = the proto field was NOT
	// set (ShouldSerialize false), so it is omitted on the wire - this is what preserves byte-exactness.
	//
	// This holds the TYPE core (constant across every spawn of a type). Fields that vary per placement (level-scaled
	// health/mana, scale, voice, ...) are overridden per-instance from NpcSpawn; transient runtime state (animation
	// phase, BccState, current HP) is emitted canonical at spawn from the live tier (WorldEntity), never stored here.
	// See docs/subsystems/npc-three-tier-architecture.md.
	//
	// ⚠️ **The column set comes from the client's SCHEMA, not from the corpus** (#646). Ten of these are NULL on all
	// 1,018 shipped rows - `RunSpeed`, `MaxRunSpeed`, `Skulk`, `MaxSkulk`, `FactionId`, `LosHeight`, `MountMesh`,
	// `OocMovementMultiplier`, `TradeskillFlags`, `ClientScryScript` - and #646's DB sweep read that as schema for a
	// decode that never landed, proposing they be dropped. They are not: `NpcStatCoreColumns.Lift` writes every one of
	// them, and NULL states the FACT that retail does not set that proto field on an NPC spawn. Since this table
	// REPLACED the captured `Data` blob, a field with no column is a field silently dropped from the rebuilt spawn the
	// first time a capture carries it. `NpcStatCoreColumnsTests.EveryStatCoreProtoField_SurvivesTheRoundTrip_EvenTheOnesNoCaptureSets`
	// drives every field set explicitly, so each column stands on its own evidence rather than on a capture's - and a
	// dropped mapping fails by NAME instead of hiding behind an all-NULL column.
	[PrimaryKey(nameof(NpcTemplateId)), NoReorder]
	public class NpcStatCore
	{
		public uint NpcTemplateId { get; set; }

		[ForeignKey(nameof(NpcTemplateId))]
		public virtual NpcTemplate NpcTemplate { get; set; } = null!;

		// The CoreCharacter-IIR header word X in [u8 0x41][u32 X][u32 msgLen] that precedes the embedded message
		// (#244). A type-stable field-presence/shape mask (RE: 0/510 types carry a mixed value): the low 16 bits
		// encode the stat-message shape (0x8203 = the npc_stats form), and a high bit toggles per type. Lifted so the
		// from-scratch spawn header can re-emit it without needing its full bit semantics decoded.
		public uint? IirHeader { get; set; }

		// --- CharacterStats (Stats) ---
		// Creature model-race code (0..98), a DIFFERENT domain from the 3-value player Race enum - stays raw uint.
		public uint? Race { get; set; }

		// The wire sex code, same enum as players (None=0 for genderless creatures, Male=2, Female=3).
		[ConstrainToEnumValues]
		public Sex? Sex { get; set; }

		public uint? Side { get; set; }

		// `CharacterStats.flags` - a real bitfield, and 0x10000001 on every NPC because **bit 28 IS the "this is an
		// NPC" bit** (#646). Measured over the whole corpus: 17,400 NPC records all carry it and all 317 player records
		// do not, while bit 0 is set on both. So the constancy across NPC types is the value being DECODED, not
		// undecoded - it is protobuf field 16, addressed by field number, so there was never an offset to get wrong.
		// Bits 3 and 5 appear only on players and only transiently, and are now named from the client (#648):
		// 3 = a trade session is open, 5 = a teleport is in flight. The whole word is the client's stat 0x000
		// "Flags" (`CharacterStatId.Flags`) - see docs/re/corecharacter-flags-bitfield.md.
		// Gated by NpcStatCoreColumnsTests.CharacterStatsFlags_Bit28_IsTheNpcBit.
		public uint? Flags { get; set; }
		public uint? HeadMesh { get; set; }
		public uint? MonsterData { get; set; }
		public uint? MonsterScale { get; set; }
		public int? Radius { get; set; }
		public uint? ChatId { get; set; }
		public uint? GuildId { get; set; }
		public uint? CarsGroup { get; set; }
		public uint? OrientationMode { get; set; }
		public uint? SoundScryScript { get; set; }
		public uint? ClientScryScript { get; set; }
		public uint? MountMesh { get; set; }
		public uint? Skulk { get; set; }
		public uint? MaxSkulk { get; set; }
		public uint? TradeskillFlags { get; set; }
		public uint? OocMovementMultiplier { get; set; }
		public uint? RunSpeed { get; set; }
		public uint? MaxRunSpeed { get; set; }
		public uint? Bcc { get; set; }
		public uint? BccState { get; set; }
		public uint? BloodType { get; set; }
		public uint? LooksPackage { get; set; }
		public uint? MountFlags { get; set; }
		public uint? MountScale { get; set; }
		public uint? MovementMultiplier { get; set; }
		public uint? VoiceSet { get; set; }
		public uint? ApprenticeLevel { get; set; }
		public uint? Level { get; set; }
		public uint? Health { get; set; }
		public uint? Life { get; set; }
		public uint? Mana { get; set; }
		public uint? MaxMana { get; set; }
		public uint? Stamina { get; set; }
		public uint? MaxStamina { get; set; }

		// --- NpcStats (npc_stats) ---
		// This NPC's own faction MEMBERSHIP, NOT a player's reputation with it, and NOT hostility. (An earlier note
		// here claimed "Community > 0 = hostile faction" - that is WRONG: community 1 is the civilian/default faction
		// shared by Citizens, vendors, Gate Guards, Shopkeepers, etc. Hostility is a faction STANDING, captured
		// separately as Disposition below.) FactionId (below) = the faction this NPC belongs to. The PLAYER's
		// per-faction standings (the client Factions window) are NOT stored yet - see Gitea #315.
		public uint? Community { get; set; }

		// The retail overhead-name DISPOSITION (cCategory) this NPC's faction has toward the player: 0 = friendly
		// (green), 1 = hostile (red), 2 = neutral (yellow). Harvested per NPC from the captured RequestAggroStatus
		// responses (NpcDispositionModule) - the SOLE correct hostility source. Drives BOTH the overhead name colour
		// and whether the sim aggros the player (only 1 = hostile aggros); NULL = unknown -> treated as non-hostile,
		// matching the client's green default. This replaces the wrong `Community > 0` aggro test (#256/aggro).
		public uint? Disposition { get; set; }
		public uint? TemplateId { get; set; }

		// The client's own `npc_type` (NpcStats field 14) - the NPC's RANK, and what the nameplate renders as its
		// Minion / MiniBoss / Boss title (#508, named in #797).
		//
		//   1 (55)   Minion    - plain creatures; ZERO are vendors and ZERO have a conversation, mostly hostile trash
		//   2 (10)   MiniBoss  - a small named group: Eerika of the Maidens, Pict Chief, Torgrim Blackfrost, Arnulf
		//                        Iceblade - and the Tortage destiny NPCs (Delia, Sakumbe, Valerius), see the warning
		//   3 (59)   Boss      - King Conan, Saddur, Ape King, Commander Vakarr, Battlemaster Torgvall, Head Poacher
		//   4 (897)  no title  - ordinary interactive NPCs; 534 are shop vendors and 513 have a conversation
		//
		// ⚠️ **It is NOT "the skull rank", and this comment used to say it was.** That came from one chat annotation -
		// `talked to npc keira (skulls next to name. like king conan)` - and Keaira (template 15090) and King Conan
		// (1917) do both carry 3, which is real evidence for those TWO NPCs and was then generalised to all 59 rows
		// sharing the value. The roster does not survive it: `Tamed Puma` and `Pleasurable Company` (a PET and a
		// Tortage civilian), `Mama Bear`, `Yeti`, `Decaying Corpse`, health spanning 12,600 to 300,000,000 inside the
		// one value. **The skull is `Normalset` (stat 205), pinned separately** - see that column.
		//
		// ⚠️ The health ordering agrees with the rank ladder (2 runs 98k-937k against 3's up-to-300M), but do NOT
		// derive loot, XP or difficulty from it: a rank TITLE is what the client draws, and nothing measured says
		// retail scales rewards off this field.
		//
		// ⭐⭐ SOLVED (#797): it is the nameplate's rank TITLE, and 1 = `Minion`, 2 = `MiniBoss`, 3 = `Boss`, 4 = no
		// title. This is a code path, not an inference from the roster:
		//
		//   `FUN_01458d40` - the SimpleChar spawn APPLY, called from `FUN_011215e0` - walks the spawn's
		//   `ProtoCoreCharacterIIR` and copies `npc_stats` (the submessage pointer at message `+0x5c`) field by field:
		//
		//       +0x18 -> SetStat(0x79)   SuperFamily          = NpcStats field 5
		//       +0x2c -> SetStat(0xCD)   Normalset            = field 10
		//       +0x30 -> SetStat(0x7F)   NPCProfessionName    = field 11
		//       +0x34 -> SetStat(0xC9)   NPCProfessionType    = field 12
		//       +0x3c -> [[dynel+0x330]+0x18]                 = field 14, THIS FIELD
		//
		//   The same window copies three fields of the SIBLING `stats` submessage (+0x58) too - `+0x34` -> LooksPacket
		//   (field 12), `+0x48` -> HeadMesh (17), `+0x58` -> BCC (21). Seven field->stat identities across two
		//   messages, every one naming the same protobuf base offset (+0x08), which puts NpcStats field 14 at `+0x3c`
		//   - the slot written into the very member the nameplate builder reads. `FUN_011d8b00` @011d8f35 then does
		//   `MOV ECX,[EBX+0x330]; MOV EAX,[ECX+0x18]` and
		//   DEC/JZ-chains 1 -> `"Minion"`, 2 -> `"MiniBoss"`, 3 -> `"Boss"`, rendering nothing otherwise. (A
		//   `Normalset == 4` short-circuits all of it to `"Pray"` - the critter tier - before the rank is consulted.)
		//
		// So the domain `{1,2,3,4}` against the client's `1|2|3` test is not a coincidence, and 4 being the untitled
		// 897-row majority of ordinary interactive NPCs is exactly right.
		//
		// ⚠️ Value 2 also holds the Tortage destiny NPCs (Delia, Sakumbe, Valerius), so "MiniBoss" genuinely does
		// appear over a friendly quest-giver unless the builder gates it on hostility somewhere outside this path.
		// That is retail's own data saying so, not a decode doubt - one in-client look at Delia settles which (#758).
		//
		// ⚠️ NULL would silently drop the rank, so every NPC renders unranked - which is why NpcStatCoreRankTests
		// gates the column as fully populated rather than trusting that it happens to be. Nothing here needed
		// building: the live spawn path already emits it (NpcSpawnBuilder -> NpcStatCoreColumns.ToNpcStats), so
		// whatever it drives was always reaching the client; the field was simply undocumented, which is why #508
		// read as missing plumbing.
		public uint? NpcType { get; set; }
		public uint? NpcFlags { get; set; }
		// The client's `normalset` (stat 205 / 0xCD) - and **this, not `npc_type`, is what draws the SKULLS** either
		// side of a target's name (#508).
		//
		// `CharPortraitRight.xml` declares `SkullLeft`/`SkullRight` as two `PortraitGUI/little_skull.tga` bitmaps
		// flanking `NameView`, both `WID_IGNORE_WHEN_HIDDEN`. The target-portrait updater (`FUN_0163eec0`) reads
		// `GetStat(0xCD, 2)` and shows BOTH skulls for a fixed set of values, hiding them for everything else:
		//
		//     8, 14, 15, 19, 20, 22, 23, 24, 26, 46, 47, 48, 49, 50, 51   -> skulls
		//     anything else                                               -> no skulls
		//
		// ⭐ Cross-checked against the chat annotation that started #508 - `talked to npc keira (skulls next to name.
		// like king conan)`. Keaira and King Conan both carry normalset 15 and both fall in the set, arrived at from
		// the client's own switch without reference to the annotation. Two independent sources, same answer.
		//
		// It selects EIGHT of our NPCs, which is what "a special rank for certain npcs" looks like - as against the
		// 59 that `npc_type == 3` was wrongly credited with. Four of the eight (the Wolf Pict Arena Master, Warlord,
		// Master Tracker and Shaman, all normalset 8) are `npc_type == 4`, i.e. ORDINARY interactive NPCs, which is
		// what rules out any reading where the skull follows npc_type.
		//
		// ⚠️ Eight is a FLOOR, not the roster: only 222 of 1,194 templates carry a normalset at all and the rest are
		// NULL, so an unharvested NPC cannot show a skull whatever it should do. Of the 15 skull-bearing values only
		// 8 and 15 occur in our data; the other 13 are unseen rather than unused.
		//
		// **What it MEANS: a creature RANK, not an appearance set.** The name reads like an animation/appearance set
		// and that guess used to sit here; the client disproves it. The nameplate builder (`FUN_011d8b00`) branches
		// on `GetStat(0xCD, 0) == 4` and, when it matches, resolves the LDB category-100 token `Pray` - whose English
		// text is **"Prey"**. Our own rows agree without being consulted: every normalset-4 NPC we have is a harmless
		// critter (Badger, Chicken, Hen, Moose, Old Mouser). An animation set does not have a member called Prey.
		//
		// The groups, characterised by their members rather than by guessing at names:
		//
		//      1 (38)  beasts and trash - Attack Dog, Bear Cub, Crow Harrier, Atzel Smuggler
		//      2 (41)  strong/named mixed - Abyssal Keeper, Ancient Crocodile, Ape King, Ares the Pyromancer
		//      3 (12)  named humanoids - Argus the Thief, Delia, Eerika of the Maidens, Pict Chief
		//      4 (16)  PREY (client-named) - Badger, Chicken, Hen, Moose, Old Mouser
		//      8 ( 4)  SKULL - the four Wolf Pict leaders (Arena Master, Warlord, Master Tracker, Shaman)
		//     15 ( 4)  SKULL - King Conan, Keaira, Frostfather, Wheel of Pain
		//     16/17/28 casters and demons  ·  32/40 guards and townsfolk  ·  9/13/30/45 assorted
		//
		// A third reader classifies the value four ways for the target portrait (`FUN_0163fff0`): 2 -> 0;
		// {10, 38, 39, 42} -> 1; {15, 22, 26, 48, 51} -> 2; everything else -> 3.
		//
		// ⚠️ **The Minion / Mini Boss / Boss labels are NOT this field, and not a stat at all.** The same nameplate
		// builder takes them from a non-stat member - dynel `+0x330` dereferenced at `+0x18`, valued 1, 2 or 3 - and
		// the client's own stat-name table has no minion/boss entry to read them from. Their LDB texts are "Minion",
		// "Mini Boss" and "Boss". Where that member is filled from is open. [V0: where the client's Minion/MiniBoss/
		// Boss nameplate rank comes from]
		public uint? Normalset { get; set; }
		public uint? SuperFamily { get; set; }
		public uint? ProfessionName { get; set; }
		public uint? ProfessionType { get; set; }
		public uint? SightSkill { get; set; }
		public uint? HearingSkill { get; set; }
		public uint? SmellingSkill { get; set; }

		// ⛔ **Not a per-type property, and the column should not exist** (#607). Retail never sends it as 0 - it is
		// UNSET or 1, a presence FLAG - and it is a **decaying STATE of one entity**, not a fact about the creature:
		// over the 4,687 entities the corpus sights more than once, 351 change their answer, 346 of those are ON for
		// the first sighting and off thereafter, and **not one ever turns ON** (`--report spawn-animation`).
		//
		// What it flags is concrete: `spawnanim` is a real per-creature MonsterData (RDB 1010201) XML attribute - the
		// client parses it into `MonsterData+0x158` through the same animation-name resolver `defaultidleanim` uses -
		// so the wire flag means *this entity is still playing its entrance animation*. The window is bounded above
		// only, tightest observation < 2.6 s, which is an animation length rather than a server timer.
		//
		// It stays a column because the SENDER cannot yet decide it: the duration is per creature (it is an asset),
		// and picking one number would invent a timing retail does not use. `PerSpawnEventModes` keeps the mode, which
		// puts us inside retail's distribution without claiming to model it. See #607.
		public uint? SpawnAnimation { get; set; }

		public uint? LosHeight { get; set; }

		// The faction this NPC belongs to (membership). See the Community note above re: player reputation (#315).
		public uint? FactionId { get; set; }

		// --- CharacterBcc (Bcc) ---
		public uint? AnimActionId { get; set; }
		public float? AnimActionTimer { get; set; }
		public bool? AnimActionBlend { get; set; }
		public uint? AnimIdleId { get; set; }
		public float? AnimIdleTimer { get; set; }
		public bool? AnimIdleBlend { get; set; }
		public uint? AnimForcedId { get; set; }
		public float? AnimForcedTimer { get; set; }
		public bool? AnimForcedBlend { get; set; }
		public uint? AnimFlags { get; set; }

		// --- root ProtoCoreCharacterIIR scalars (outside the sub-messages) ---
		public bool? CanFall { get; set; }

		// The entity's movement mode: 1, 2 or 3. Sent TWICE in one spawn body - as this protobuf field and as the
		// post-IIR header's third word - which is why there used to be a second column (`MinimalPostIirWord2`) holding
		// the same number, lifted from a different representative spawn and disagreeing with this one on 45 of 368
		// types. There is one value, so there is one column, and `NpcSpawnBuilder` derives the post-IIR word from it
		// (Gitea #605; the identity is measured in `--report field-predictors` and documented on
		// `SpawnPostIir.MovementMode`).
		//
		// ⚠️ Rolled per SPAWN EVENT, not a property of the type: only ~62% of templates hold one value across their
		// sightings even when restricted to freshly-spawned NPCs. What is stored is therefore the type's MODAL value
		// over its own captured sightings - a measurement, not the corpus-wide mode, which is the line #602 drew.
		public uint? MovementMode { get; set; }

		// --- harvested, not lifted from the spawn body ---

		// #675: the NPC's COMBAT ARCHETYPE, as a <see cref="Core.Enums.ConanClass" /> - which `combo_npc_<class>_*`
		// family retail lets this template swing beside its generic row. NULL means no capture ever saw this template
		// swing a class family, which is the honest answer for 98% of them; the runtime then falls back to the generic
		// set alone.
		//
		// ⚠️ Not in the spawn body, not in the RDB and not derivable from anything else we hold: `Community`,
		// `MonsterData`, `SuperFamily`, `NpcType`, `Race`, `VoiceSet`, `CarsGroup` and `Flags` all cut straight across
		// the eight observed families (MonsterData 140326 alone carries barbarians, guardians and assassins), and
		// `ProfessionType` is NULL on all but one of the captured actors. So it is HARVESTED: the class is read off
		// the client's own `Combos.NpcClass` on every class-family combo the corpus catches this template swinging.
		//
		// ⭐ It is a per-TEMPLATE property and the corpus proves it: over the 30 templates caught swinging one, not a
		// single template swings two different classes - Hired Bodyguard swings `guardian_shield` AND
		// `guardian_polearm`, Poacher swings `barbarian_2h` AND `barbarian_dual`, Argus/Wharf Rat Thug swing
		// `barbarian_2h` AND the `heimdall_fatality` rows the client marks `npcclass=18` Barbarian. Zero conflicts.
		// It is also NOT a per-NAME property: `Pillager` 8756 is a Guardian and `Pillager` 14040 a Dark Templar,
		// and `Scavenger Lackey` splits Conqueror (12137/14330) from Assassin (12141/14331).
		//
		// ⭐ And it explains the one case the old weapon heuristic could not: a shield does NOT make a Guardian -
		// `Vanir Champion Elite` wears a shield and swings the Barbarian fatality rows, which is why the shield
		// bucket was refuted when read as a weapon rule.
		public int? ComboClass { get; set; }
	}
}
