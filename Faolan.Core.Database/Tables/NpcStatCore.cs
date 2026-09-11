using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	/// <summary>
	///     The type-level CharacterIIR stat core, as typed columns so an NPC spawn body can be constructed from the DB.
	///     A 1:1 side table keyed to its <see cref="NpcTemplate" /> type (like Character &lt;-&gt; CharacterMaxStats).
	///     Property names mirror the protobuf field names in ProtoCoreCharacterIIR's Stats (CharacterStats) / npc_stats
	///     (NpcStats) / Bcc (CharacterBcc) sub-messages exactly, so <c>NpcStatCoreColumns</c> maps them by name with no
	///     per-field code. Nullable throughout: null = the proto field is not set (ShouldSerialize false), so it is
	///     omitted on the wire - this is what preserves byte-exactness.
	/// </summary>
	/// <remarks>
	///     This holds the type core (constant across every spawn of a type). Fields that vary per placement
	///     (level-scaled health/mana, scale, voice, ...) are overridden per-instance from <see cref="NpcSpawn" />;
	///     transient runtime state (animation phase, BccState, current HP) is emitted canonical at spawn from the live
	///     tier (WorldEntity), never stored here. See <c>docs/subsystems/npc-three-tier-architecture.md</c>.
	///     <para>
	///         The column set comes from the client's schema, not from the corpus. Ten of these are null on every
	///         shipped row - <c>RunSpeed</c>, <c>MaxRunSpeed</c>, <c>Skulk</c>, <c>MaxSkulk</c>, <c>FactionId</c>,
	///         <c>LosHeight</c>, <c>MountMesh</c>, <c>OocMovementMultiplier</c>, <c>TradeskillFlags</c>,
	///         <c>ClientScryScript</c> - and must not be dropped for it: <c>NpcStatCoreColumns.Lift</c> writes every one
	///         of them, and null states the fact that retail does not set that proto field on an NPC spawn. Since this
	///         table is the sole source of the rebuilt spawn, a field with no column is a field silently dropped the
	///         first time a capture carries it.
	///         <c>NpcStatCoreColumnsTests.EveryStatCoreProtoField_SurvivesTheRoundTrip_EvenTheOnesNoCaptureSets</c>
	///         drives every field set explicitly, so a dropped mapping fails by name instead of hiding behind an
	///         all-null column.
	///     </para>
	/// </remarks>
	[PrimaryKey(nameof(NpcTemplateId)), NoReorder]
	public class NpcStatCore
	{
		/// <summary>
		///     The <see cref="CarsGroup" /> of a creature that fights. Retail treats it as a live property, not just a
		///     spawn field: when Saddur's dialogue turns him on the player it restates his group to this value with a
		///     <c>StatIIR</c>, and only then does the client draw him hostile and let him be hit. Measured over the
		///     whole corpus, every character <c>CarsGroup</c> record carrying 3 is one of the three captured Saddur
		///     fights. It is also the collision group the last-resort disposition tier keys on
		///     (<c>NpcDispositionTiers</c>): groups 0/1 are the named townsfolk, 3 the mobs.
		/// </summary>
		public const uint MobCarsGroup = 3;

		public uint NpcTemplateId { get; set; }

		[ForeignKey(nameof(NpcTemplateId))]
		public virtual NpcTemplate NpcTemplate { get; set; } = null!;

		/// <summary>
		///     The CoreCharacter-IIR header word X in [u8 0x41][u32 X][u32 msgLen] that precedes the embedded message.
		///     A type-stable field-presence/shape mask (0/510 types carry a mixed value): the low 16 bits encode the
		///     stat-message shape (0x8203 = the npc_stats form), and a high bit toggles per type. Lifted so the
		///     from-scratch spawn header can re-emit it without needing its full bit semantics decoded.
		/// </summary>
		public uint? IirHeader { get; set; }

		// --- CharacterStats (Stats) ---
		// Creature model-race code (0..98), a different domain from the 3-value player Race enum - stays raw uint.
		public uint? Race { get; set; }

		// The wire sex code, same enum as players (None=0 for genderless creatures, Male=2, Female=3).
		[ConstrainToEnumValues]
		public Sex? Sex { get; set; }

		public uint? Side { get; set; }

		/// <summary>
		///     <c>CharacterStats.flags</c> - a real bitfield, and 0x10000001 on every NPC because bit 28 is the "this is
		///     an NPC" bit: 17,400 NPC records all carry it and all 317 player records do not, while bit 0 is set on
		///     both. Bits 3 and 5 appear only on players and only transiently, named from the client: 3 = a trade
		///     session is open, 5 = a teleport is in flight. The whole word is the client's stat 0x000 "Flags"
		///     (<see cref="CharacterStatId.Flags" />) - see <c>docs/re/corecharacter-flags-bitfield.md</c>. Gated by
		///     <c>NpcStatCoreColumnsTests.CharacterStatsFlags_Bit28_IsTheNpcBit</c>.
		/// </summary>
		public uint? Flags { get; set; }
		public uint? HeadMesh { get; set; }
		public uint? MonsterData { get; set; }
		public uint? MonsterScale { get; set; }
		public int? Radius { get; set; }
		public uint? ChatId { get; set; }
		public uint? GuildId { get; set; }
		// The collision/response group (`cars_group`, spawn proto field 5). Not an aggression field, but it separates
		// the populations: groups 0/1 hold the named townsfolk, 3 the mobs (see MobCarsGroup).
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
		/// <summary>
		///     This NPC's own faction membership - not a player's reputation with it, and not hostility. Community 1 is
		///     the civilian/default faction shared by Citizens, vendors, Gate Guards, Shopkeepers, etc. Hostility is a
		///     faction standing, captured separately as <see cref="Disposition" />.
		/// </summary>
		public uint? Community { get; set; }

		/// <summary>
		///     The retail overhead-name disposition (cCategory) this NPC's faction has toward the player: 0 = friendly
		///     (green), 1 = hostile (red), 2 = neutral (yellow). Harvested per NPC from the captured RequestAggroStatus
		///     responses (<c>NpcDispositionModule</c>) - the sole correct hostility source. Drives both the overhead
		///     name colour and whether the sim aggros the player (only 1 = hostile aggros); null = unknown, treated as
		///     non-hostile, matching the client's green default. <c>Community &gt; 0</c> is not a hostility test.
		/// </summary>
		public uint? Disposition { get; set; }
		public uint? TemplateId { get; set; }

		/// <summary>
		///     The client's own <c>npc_type</c> (NpcStats field 14) - the NPC's rank, and what the nameplate renders as
		///     its Minion / MiniBoss / Boss title:
		///     <code>
		///     1 (55)   Minion    - plain creatures; zero are vendors and zero have a conversation, mostly hostile trash
		///     2 (10)   MiniBoss  - a small named group: Eerika of the Maidens, Pict Chief, Torgrim Blackfrost, Arnulf
		///                         Iceblade - and the Tortage destiny NPCs (Delia, Sakumbe, Valerius)
		///     3 (59)   Boss      - King Conan, Saddur, Ape King, Commander Vakarr, Battlemaster Torgvall, Head Poacher
		///     4 (897)  no title  - ordinary interactive NPCs; 534 are shop vendors and 513 have a conversation
		///     </code>
		/// </summary>
		/// <remarks>
		///     <para>
		///         This is a code path, not an inference from the roster. <c>FUN_01458d40</c> - the SimpleChar spawn
		///         apply, called from <c>FUN_011215e0</c> - walks the spawn's <c>ProtoCoreCharacterIIR</c> and copies
		///         <c>npc_stats</c> (the submessage pointer at message <c>+0x5c</c>) field by field:
		///         <code>
		///         +0x18 -> SetStat(0x79)   SuperFamily          = NpcStats field 5
		///         +0x2c -> SetStat(0xCD)   Normalset            = field 10
		///         +0x30 -> SetStat(0x7F)   NPCProfessionName    = field 11
		///         +0x34 -> SetStat(0xC9)   NPCProfessionType    = field 12
		///         +0x3c -> [[dynel+0x330]+0x18]                 = field 14, this field
		///         </code>
		///         The same window copies three fields of the sibling <c>stats</c> submessage (+0x58) too -
		///         <c>+0x34</c> -&gt; LooksPacket (field 12), <c>+0x48</c> -&gt; HeadMesh (17), <c>+0x58</c> -&gt; BCC
		///         (21). <c>FUN_011d8b00</c> @011d8f35 then does <c>MOV ECX,[EBX+0x330]; MOV EAX,[ECX+0x18]</c> and
		///         DEC/JZ-chains 1 -&gt; "Minion", 2 -&gt; "MiniBoss", 3 -&gt; "Boss", rendering nothing otherwise (a
		///         <c>Normalset == 4</c> short-circuits all of it to "Pray" - the critter tier - before the rank is
		///         consulted).
		///     </para>
		///     <para>
		///         It is not the skull rank: the skulls are <see cref="Normalset" /> (stat 205). The health ordering
		///         agrees with the rank ladder (2 runs 98k-937k against 3's up-to-300M), but do not derive loot, XP or
		///         difficulty from it: a rank title is what the client draws, and nothing measured says retail scales
		///         rewards off this field.
		///     </para>
		///     <para>
		///         Value 2 also holds the Tortage destiny NPCs (Delia, Sakumbe, Valerius), so "MiniBoss" genuinely
		///         appears over a friendly quest-giver unless the builder gates it on hostility somewhere outside this
		///         path. [V0: whether the nameplate rank title is gated on hostility in-client]
		///     </para>
		///     <para>
		///         Null would silently drop the rank, so every NPC renders unranked - which is why
		///         <c>NpcStatCoreRankTests</c> gates the column as fully populated. The live spawn path emits it
		///         (<c>NpcSpawnBuilder</c> -&gt; <c>NpcStatCoreColumns.ToNpcStats</c>).
		///     </para>
		/// </remarks>
		public uint? NpcType { get; set; }
		public uint? NpcFlags { get; set; }
		/// <summary>
		///     The client's <c>normalset</c> (stat 205 / 0xCD) - and this, not <c>npc_type</c>, is what draws the
		///     skulls either side of a target's name. <c>CharPortraitRight.xml</c> declares <c>SkullLeft</c>/
		///     <c>SkullRight</c> as two <c>PortraitGUI/little_skull.tga</c> bitmaps flanking <c>NameView</c>, both
		///     <c>WID_IGNORE_WHEN_HIDDEN</c>. The target-portrait updater (<c>FUN_0163eec0</c>) reads
		///     <c>GetStat(0xCD, 2)</c> and shows both skulls for a fixed set of values, hiding them for everything else:
		///     <code>
		///     8, 14, 15, 19, 20, 22, 23, 24, 26, 46, 47, 48, 49, 50, 51   -> skulls
		///     anything else                                               -> no skulls
		///     </code>
		/// </summary>
		/// <remarks>
		///     <para>
		///         Cross-checked against a chat annotation - "talked to npc keira (skulls next to name. like king
		///         conan)": Keaira and King Conan both carry normalset 15 and both fall in the set, arrived at from the
		///         client's own switch without reference to the annotation. It selects eight of our NPCs; four of them
		///         (the Wolf Pict Arena Master, Warlord, Master Tracker and Shaman, all normalset 8) are
		///         <c>npc_type == 4</c>, i.e. ordinary interactive NPCs, which rules out any reading where the skull
		///         follows npc_type.
		///     </para>
		///     <para>
		///         Eight is a floor, not the roster: only 222 of 1,194 templates carry a normalset at all and the rest
		///         are null, so an unharvested NPC cannot show a skull whatever it should do. Of the 15 skull-bearing
		///         values only 8 and 15 occur in our data; the other 13 are unseen rather than unused.
		///     </para>
		///     <para>
		///         What it means: a creature rank, not an appearance set. The nameplate builder (<c>FUN_011d8b00</c>)
		///         branches on <c>GetStat(0xCD, 0) == 4</c> and, when it matches, resolves the LDB category-100 token
		///         <c>Pray</c> - whose English text is "Prey". Every normalset-4 NPC we have is a harmless critter
		///         (Badger, Chicken, Hen, Moose, Old Mouser). The groups, characterised by their members:
		///         <code>
		///          1 (38)  beasts and trash - Attack Dog, Bear Cub, Crow Harrier, Atzel Smuggler
		///          2 (41)  strong/named mixed - Abyssal Keeper, Ancient Crocodile, Ape King, Ares the Pyromancer
		///          3 (12)  named humanoids - Argus the Thief, Delia, Eerika of the Maidens, Pict Chief
		///          4 (16)  prey (client-named) - Badger, Chicken, Hen, Moose, Old Mouser
		///          8 ( 4)  skull - the four Wolf Pict leaders (Arena Master, Warlord, Master Tracker, Shaman)
		///         15 ( 4)  skull - King Conan, Keaira, Frostfather, Wheel of Pain
		///         16/17/28 casters and demons  ·  32/40 guards and townsfolk  ·  9/13/30/45 assorted
		///         </code>
		///         A third reader classifies the value four ways for the target portrait (<c>FUN_0163fff0</c>):
		///         2 -&gt; 0; {10, 38, 39, 42} -&gt; 1; {15, 22, 26, 48, 51} -&gt; 2; everything else -&gt; 3.
		///     </para>
		///     <para>
		///         The Minion / Mini Boss / Boss labels are not this field: they come from <see cref="NpcType" />
		///         (NpcStats field 14), which the nameplate builder reads off a non-stat member (dynel <c>+0x330</c>
		///         dereferenced at <c>+0x18</c>).
		///     </para>
		/// </remarks>
		public uint? Normalset { get; set; }
		public uint? SuperFamily { get; set; }
		public uint? ProfessionName { get; set; }
		public uint? ProfessionType { get; set; }
		public uint? SightSkill { get; set; }
		public uint? HearingSkill { get; set; }
		public uint? SmellingSkill { get; set; }

		/// <summary>
		///     Not a per-type property. Retail never sends it as 0 - it is unset or 1, a presence flag - and it is a
		///     decaying state of one entity, not a fact about the creature: over the 4,687 entities the corpus sights
		///     more than once, 351 change their answer, 346 of those are on for the first sighting and off thereafter,
		///     and not one ever turns on (<c>--report spawn-animation</c>).
		/// </summary>
		/// <remarks>
		///     <para>
		///         What it flags is concrete: <c>spawnanim</c> is a real per-creature MonsterData (RDB 1010201) XML
		///         attribute - the client parses it into <c>MonsterData+0x158</c> through the same animation-name
		///         resolver <c>defaultidleanim</c> uses - so the wire flag means "this entity is still playing its
		///         entrance animation". The window is bounded above only, tightest observation &lt; 2.6 s, which is an
		///         animation length rather than a server timer.
		///     </para>
		///     <para>
		///         Retail sets it at (re)creation: <c>--report spawn-animation</c>'s respawn test matches each captured
		///         death to the same template coming back within 15 m of where it fell, and finds 20 of 35 such
		///         respawns flagged against 8.1 expected from those same templates' other first-sightings. The
		///         cleanest lines are the solitary animals, where a positional match cannot pick up a pack-mate:
		///         Panther 3/3 against 1/68, Wild Cow 2/2 against 0/147, Wild Pig 1/1 against 0/83.
		///     </para>
		///     <para>
		///         The sender honors that, and only that: <c>WorldBroadcaster.BroadcastNpcRespawn</c> stamps the flag
		///         onto the respawn delivery (<c>NpcSpawnBuilder.WithSpawnAnimation</c>) and no other path does. That
		///         deliberately under-sends - retail also flags the ~6-9% of ordinary first-sightings whose creature
		///         respawned before the player arrived - because the window is unmeasurable: the death-&gt;delivery gap
		///         has an unknown respawn timer inside it, so no flagged delivery bounds the window from below, and
		///         picking a duration would invent a timing retail does not use.
		///     </para>
		///     <para>
		///         This column keeps its imported mode-over-sightings value, a per-type approximation of a per-entity
		///         state; retiring it needs the window, and the respawn stamp rides on top of it either way. The other
		///         reading of the clear - "cleared once delivered" - is untestable from the corpus:
		///         <c>CapturedPacket.Port</c> is the game side's port, so both clients of a two-client dump share it,
		///         and nothing in a dump names the recipient.
		///     </para>
		/// </remarks>
		public uint? SpawnAnimation { get; set; }

		public uint? LosHeight { get; set; }

		/// <summary>
		///     The faction this NPC belongs to (membership). See <see cref="Community" />.
		/// </summary>
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

		/// <summary>
		///     The entity's movement mode: 1, 2 or 3. Sent twice in one spawn body - as this protobuf field and as the
		///     post-IIR header's third word - so there is one column, and <c>NpcSpawnBuilder</c> derives the post-IIR
		///     word from it (the identity is measured in <c>--report field-predictors</c> and documented on
		///     <c>SpawnPostIir.MovementMode</c>).
		/// </summary>
		/// <remarks>
		///     Rolled per spawn event, not a property of the type: only ~62% of templates hold one value across their
		///     sightings even when restricted to freshly-spawned NPCs. What is stored is therefore the type's modal
		///     value over its own captured sightings - a measurement, not the corpus-wide mode.
		/// </remarks>
		public uint? MovementMode { get; set; }

		// --- harvested, not lifted from the spawn body ---

		/// <summary>
		///     The NPC's combat archetype, as a <see cref="Core.Enums.ConanClass" /> - which <c>combo_npc_&lt;class&gt;_*</c>
		///     family retail lets this template swing beside its generic row. Null means no capture ever saw this
		///     template swing a class family, which is the honest answer for 98% of them; the runtime then falls back
		///     to the generic set alone.
		/// </summary>
		/// <remarks>
		///     Not in the spawn body, not in the RDB and not derivable from anything else we hold: <c>Community</c>,
		///     <c>MonsterData</c>, <c>SuperFamily</c>, <c>NpcType</c>, <c>Race</c>, <c>VoiceSet</c>, <c>CarsGroup</c> and
		///     <c>Flags</c> all cut straight across the eight observed families (MonsterData 140326 alone carries
		///     barbarians, guardians and assassins), and <c>ProfessionType</c> is null on all but one of the captured
		///     actors. So it is harvested: the class is read off the client's own <c>Combos.NpcClass</c> on every
		///     class-family combo the corpus catches this template swinging.
		///     <para>
		///         It is a per-template property and the corpus proves it: over the 30 templates caught swinging one,
		///         not a single template swings two different classes - Hired Bodyguard swings <c>guardian_shield</c>
		///         and <c>guardian_polearm</c>, Poacher swings <c>barbarian_2h</c> and <c>barbarian_dual</c>,
		///         Argus/Wharf Rat Thug swing <c>barbarian_2h</c> and the <c>heimdall_fatality</c> rows the client marks
		///         <c>npcclass=18</c> Barbarian. It is not a per-name property: <c>Pillager</c> 8756 is a Guardian and
		///         <c>Pillager</c> 14040 a Dark Templar, and <c>Scavenger Lackey</c> splits Conqueror (12137/14330) from
		///         Assassin (12141/14331). Nor is it a weapon rule: a shield does not make a Guardian -
		///         <c>Vanir Champion Elite</c> wears a shield and swings the Barbarian fatality rows.
		///     </para>
		/// </remarks>
		public int? ComboClass { get; set; }
	}
}
