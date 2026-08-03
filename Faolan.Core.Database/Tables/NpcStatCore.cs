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

		// The client's own `npc_type` (NpcStats field 14) - the NPC's KIND, and the field that decides the nameplate
		// rank decoration: **3 is the boss/elite rank the client draws SKULLS next to** (#508).
		//
		// Identified from the chat-annotated gameplay capture, which says `talked to npc keira (skulls next to name.
		// like king conan)`. Keaira (template 15090) and King Conan (1917) are the pair that annotation compares, and
		// they are the ONLY thing it names - both carry npc_type 3. The 56 rows sharing that value then read as a
		// roster of exactly what the marker is for: Harbinger of Kao, Wulfhere, Hadrathus, Thunder River Demon Lord,
		// Verdandi the Ghost King, The Ravager, Saddur, Ape King, Commander Vakarr/Ramn/Magni, Yeti, Mama Bear.
		//
		// The four observed values, characterised by what each group can DO rather than by guessing at names:
		//   1 (55)  plain creatures - ZERO are vendors and ZERO have a conversation; mostly hostile trash
		//   2 (10)  a small named group, mixing Tortage destiny NPCs (Delia, Sakumbe, Valerius) with named enemies
		//           (Eerika of the Maidens, Pict Chief, Torgrim Blackfrost, Arnulf Iceblade) - see the V0 below
		//   3 (56)  named bosses / elites - the SKULL rank; 27 of 56 hostile, only 3 have any conversation
		//   4 (897) ordinary interactive NPCs - 534 are shop vendors and 513 have a conversation
		//
		// ⚠️ NULL would silently drop the rank, so every NPC renders unranked - which is why NpcStatCoreRankTests
		// gates the column as fully populated rather than trusting that it happens to be. Nothing here needed
		// building: the live spawn path already emits it (NpcSpawnBuilder -> NpcStatCoreColumns.ToNpcStats), so the
		// skull was always reaching the client; the field was simply undocumented, which is why #508 read as missing
		// plumbing. Types 1 vs 2 are not fully separated - [V0: what npc_type 1 and 2 mean].
		public uint? NpcType { get; set; }
		public uint? NpcFlags { get; set; }
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
	}
}
