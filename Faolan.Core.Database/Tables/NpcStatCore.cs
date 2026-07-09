using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// The type-level CharacterIIR stat CORE (#244), lifted off the captured `Data` blob into typed columns so an NPC
	// spawn body can be CONSTRUCTED from the DB instead of spliced from a capture. A 1:1 side table keyed to its `Npc`
	// type (like Character <-> CharacterMaxStats). Property names mirror the protobuf field names in
	// ProtoCoreCharacterIIR's Stats (CharacterStats) / npc_stats (NpcStats) / Bcc (CharacterBcc) sub-messages exactly,
	// so NpcStatCoreCodec maps them by name with no per-field code. Nullable throughout: null = the proto field was NOT
	// set (ShouldSerialize false), so it is omitted on the wire - this is what preserves byte-exactness.
	//
	// This holds the TYPE core (constant across every spawn of a type). Fields that vary per placement (level-scaled
	// health/mana, scale, voice, ...) are overridden per-instance from NpcSpawn; transient runtime state (animation
	// phase, BccState, current HP) is emitted canonical at spawn from the live tier (WorldEntity), never stored here.
	// See docs/npc-three-tier-architecture.md.
	[PrimaryKey(nameof(NpcId)), NoReorder]
	public class NpcStatCore
	{
		public uint NpcId { get; set; }

		[ForeignKey(nameof(NpcId))]
		public virtual Npc Npc { get; set; }

		// The CoreCharacter-IIR header word X in [u8 0x41][u32 X][u32 msgLen] that precedes the embedded message
		// (#244). A type-stable field-presence/shape mask (RE: 0/510 types carry a mixed value): the low 16 bits
		// encode the stat-message shape (0x8203 = the npc_stats form), and a high bit toggles per type. Lifted so the
		// from-scratch spawn header can re-emit it without needing its full bit semantics decoded.
		public uint? IirHeader { get; set; }

		// The recordCount a NO-MORPH spawn of this type ships in its fresh minimal post-IIR (#243): observed as 1 or 2.
		// A TYPE-STABLE constant (no type is ever seen with both) whose exact meaning isn't decoded - it is NOT an
		// animal-vs-humanoid split (both values carry a mix of people and creatures: Kalanthes/merchants are 1,
		// Saddur/guards are 2, while Wolf/Chicken are 2 and Cobra/Crocodile are 1). Derived at import from a minimal-form
		// capture. Lets a no-morph spawn build its post-IIR from scratch - BuildMinimalPostIir(rc) - instead of splicing
		// the captured Data blob. Null for appearance-bearing types (they use their morph rows) and for types only ever
		// seen in a runtime-bearing form (their fresh recordCount is unknown -> stay on Data).
		public uint? MinimalPostIirRecordCount { get; set; }

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
		// This NPC's own faction MEMBERSHIP, NOT a player's reputation with it. Community: 0 = neutral/friendly,
		// > 0 = a hostile faction (drives the red/green overhead name + aggro). FactionId (below) = the faction this
		// NPC belongs to. The PLAYER's per-faction standings (the client Factions window) are NOT stored yet - see
		// Gitea #315 (would need a CharacterFaction table + faction catalog).
		public uint? Community { get; set; }
		public uint? TemplateId { get; set; }
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
		public uint? MovementMode { get; set; }
	}
}
