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

		// The client's own `npc_type` (NpcStats field 14) - the NPC's KIND, which is one of the inputs to the
		// nameplate's rank decoration (#508).
		//
		// ⚠️ **It is NOT "the skull rank", and this comment used to say it was.** The identification came from one
		// chat annotation - `talked to npc keira (skulls next to name. like king conan)` - and Keaira (template
		// 15090) and King Conan (1917) do both carry npc_type 3. That is real evidence for those TWO NPCs and it was
		// then generalised to all 59 rows sharing the value, which does not survive reading the roster: it contains
		// `Tamed Puma` and `Pleasurable Company` (a PET and a Tortage civilian), `Mama Bear`, `Yeti` and
		// `Decaying Corpse`, and its health spans 12,600 to 300,000,000 inside the one value. Melvin, who has played
		// the game, says the skull is a special rank carried by CERTAIN npcs and that a 59-row list is far too long.
		//
		// So 3 is a superset of, or merely correlated with, whatever the client actually reads to draw a skull. The
		// client models at least three tiers - it ships `MiniBoss` and `Boss` tokens, SEPARATE `EvtDeathMiniBoss` /
		// `EvtDeathFullBoss` script events, and separate `miniboss_icon` / `boss_icon` nameplate icons beside a
		// `SkullLeft`/`SkullRight` widget pair - and nothing here has been mapped to those. There is also an
		// unimported `Difficulty` stat (1398 / 0x576) that no NPC record has been checked for.
		//
		// ⚠️ Do NOT build loot, XP or difficulty on `npc_type == 3` meaning boss until the selector is pinned: find
		// what the client reads to show `SkullLeft`/`SkullRight` and to choose between the two icons. The widgets are
		// constructed in the target-window builder (`FUN_0163b370`, stored at `+0x13b`/`+0x13c`); their VISIBILITY is
		// set elsewhere and that writer is the answer.
		//
		// The four observed values, characterised by what each group can DO rather than by guessing at names:
		//   1 (55)  plain creatures - ZERO are vendors and ZERO have a conversation; mostly hostile trash
		//   2 (10)  a small named group, mixing Tortage destiny NPCs (Delia, Sakumbe, Valerius) with named enemies
		//           (Eerika of the Maidens, Pict Chief, Torgrim Blackfrost, Arnulf Iceblade) - see the V0 below
		//   3 (59)  named/special NPCs, bosses among them - but see the warning above; NOT simply "the skull rank"
		//   4 (897) ordinary interactive NPCs - 534 are shop vendors and 513 have a conversation
		//
		// ⭐ It is the nameplate's Minion / Mini Boss / Boss TITLE (#758). The nameplate builder `FUN_011d8b00` reads a
		// non-stat member - dynel `+0x330` dereferenced at `+0x18` - and renders those three LDB category-100 texts
		// for values 1, 2 and 3, and nothing at all otherwise. That is this field: the DOMAIN is exact (`{1,2,3,4}`
		// against a `1|2|3` test, with 4 the untitled 897-row majority of ordinary interactive NPCs), the MEMBERSHIP
		// matches the labels (3 = King Conan / Saddur / Ape King / Commander Vakarr; 2 = Taskmaster Olaf / Pict Chief
		// / Argus the Thief; 1 = Rabbit / Hen / Spiderling / Target Dummy), the health ordering agrees (2 runs
		// 98k-937k against 3's up-to-300M), and the title RENDERS ON OUR SERVER - so whatever fills `+0x330` is
		// already something we send, and this is the only 4-valued NPC field we send that could feed it.
		//
		// ⚠️ Value 2 also holds the Tortage destiny NPCs (Delia, Sakumbe, Valerius), and "Mini Boss" over a friendly
		// quest-giver reads wrong. Either the builder gates the title on hostility outside the decompiled fragment,
		// or it genuinely shows there - one in-client look at Delia's nameplate settles it. Recorded rather than
		// smoothed over; see docs/protocol/gaps.md.
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
