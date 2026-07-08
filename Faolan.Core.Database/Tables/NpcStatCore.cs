namespace Faolan.Core.Database.Tables
{
	// The type-level CharacterIIR stat CORE (#244), lifted off the captured `Data` blob into typed columns so an NPC
	// spawn body can be CONSTRUCTED from the DB instead of spliced from a capture. An EF owned type (its columns live
	// on the owner row - `Npc` for the template core). Property names mirror the protobuf field names in
	// ProtoCoreCharacterIIR's Stats (CharacterStats) / npc_stats (NpcStats) / Bcc (CharacterBcc) sub-messages exactly,
	// so NpcStatCoreCodec maps them by name with no per-field code. Nullable throughout: null = the proto field was NOT
	// set (ShouldSerialize false), so it is omitted on the wire - this is what preserves byte-exactness.
	//
	// This holds the TYPE core (constant across every spawn of a type). Fields that vary per placement (level-scaled
	// health/mana, scale, voice, ...) are overridden per-instance from NpcSpawn; transient runtime state (animation
	// phase, BccState, current HP) is emitted canonical at spawn from the live tier (WorldEntity), never stored here.
	// See docs/npc-three-tier-architecture.md.
	public class NpcStatCore
	{
		// --- CharacterStats (Stats) ---
		public uint? Race { get; set; }
		public uint? Sex { get; set; }
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
