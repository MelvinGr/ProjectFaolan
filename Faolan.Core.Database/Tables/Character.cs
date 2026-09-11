using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	/// <summary>
	///     A player character. Names are unique per realm, not globally: realm Crom and realm Fury may each have a
	///     "Bob".
	/// </summary>
	[Index(nameof(RealmId), nameof(Name), IsUnique = true)]
	public class Character : Base3DEntity
	{
		public uint AccountId { get; set; }

		[ForeignKey(nameof(AccountId))]
		public virtual Account Account { get; set; } = null!;

		/// <summary>
		///     Bind / recall point: the resurrection point the Totem of Origin returns the character to. A reference to
		///     a <see cref="RespawnPoint" /> the player has discovered (<see cref="CharacterRespawnPoint" />), set by
		///     the <c>.bind</c> GM command. It answers the client's RequestBindLocation poll (the Totem destination
		///     playfield) and resolves to the point's map+position on recall. Null = unbound (falls back to the
		///     character's current map).
		/// </summary>
		public uint? BindRespawnPointId { get; set; }

		[ForeignKey(nameof(BindRespawnPointId))]
		public virtual RespawnPoint? BindRespawnPoint { get; set; }

		/// <summary>
		///     Guild membership; null = guildless. Rank 0 = fresh member (retail's invite-accept record), higher =
		///     promoted; the guild creator gets GuildLeaderRank. See <c>tools/capture/decode_guild.py</c> for the wire
		///     records.
		/// </summary>
		public uint? GuildId { get; set; }

		[ForeignKey(nameof(GuildId))]
		public virtual Guild? Guild { get; set; }

		/// <summary>
		///     Required: a character always lives on a map; Tortage Beach is the universal fallback world.
		/// </summary>
		public uint MapId { get; set; } = Map.TortageBeachId;

		/// <summary>
		///     Where a PvP-minigame teleport took this character from. Written when the match countdown moves them in,
		///     cleared by the live leave's teleport-back and by the login redirect that consumes it. Non-null at login
		///     means the character last saved inside a match that no longer runs (a mid-match disconnect, a server
		///     restart) - the world entry then goes here instead of stranding them on the minigame map.
		/// </summary>
		public uint? PvpReturnMapId { get; set; }

		public float PvpReturnX { get; set; }

		public float PvpReturnY { get; set; }

		public float PvpReturnZ { get; set; }

		[ForeignKey(nameof(MapId))]
		public virtual Map Map { get; set; } = null!;

		/// <summary>
		///     The mount the character is currently riding (a Mounts row id; null = on foot). Persisted so the mount
		///     survives a relog and a trip through a no-mount area: it is set when the player summons a mount and
		///     cleared on a voluntary dismount, but not on the auto-dismount the client does when entering a no-mount
		///     playfield - so re-entering a mount-allowed area (or logging back in there) re-seats the same mount.
		///     Re-applied on world entry only when the destination map allows mounts (Map.PlayfieldFlags mount bit).
		/// </summary>
		public uint? MountId { get; set; }

		/// <summary>
		///     Required: set on creation (CreateCharacter) and by the capture importer.
		/// </summary>
		public uint RealmId { get; set; }

		[ForeignKey(nameof(RealmId))]
		public virtual Realm Realm { get; set; } = null!;

		public virtual CharacterBody? Body { get; set; }

		/// <summary>
		///     The appearance resources chosen in the create editor (hair/beard mesh, hair/eye/skin color, tattoos,
		///     scars), in editor send order. Resolved via <see cref="MorphTarget" /> into the spawn scaffold's weighted
		///     entries - which, with Body's morph sliders, is the whole of the spawn's appearance entry table, derived
		///     at send time (<c>PlayerSpawnScaffoldRows.BuildAppearanceEntries</c>).
		/// </summary>
		public virtual ICollection<CharacterBodyResource> BodyResources { get; set; } = [];
		public virtual ICollection<CharacterBuff> Buffs { get; set; } = [];

		/// <summary>
		///     The unlocked feat (talent-tree) ids - the spawn's pre-IIR feat list, distinct from <see cref="Skills" />
		///     (the post-IIR ability scaffold). See <see cref="CharacterFeat" /> + <c>docs/subsystems/feats.md</c>.
		/// </summary>
		public virtual ICollection<CharacterFeat> Feats { get; set; } = [];

		/// <summary>
		///     The character's saved feat specs. One unnamed spec is the normal case; a feat belongs to exactly one of
		///     these via <see cref="CharacterFeat.SpecIndex" />.
		/// </summary>
		public virtual ICollection<CharacterFeatSpec> FeatSpecs { get; set; } = [];

		/// <summary>
		///     Every item the character holds - worn (container Equipped, slot = the equip slot id) and carried
		///     (bag/resource/quest) alike, one row per item. Drives the FullChar inventory snapshot and the spawn's
		///     visible gear. See <see cref="ItemInstance" /> / <c>docs/re/player-spawn-generation.md</c>.
		/// </summary>
		public virtual ICollection<ItemInstance> Items { get; set; } = [];

		// Per-character progression/state collections (inverse sides of the matching child tables, all cascade-delete
		// with the character). Quests/Tags are the quest-state machine, RespawnPoints the resurrection points the
		// character has discovered, Shortcuts the hotbar, Spells the learned spell set re-granted on login.
		public virtual ICollection<CharacterQuest> Quests { get; set; } = [];
		public virtual ICollection<CharacterRespawnPoint> RespawnPoints { get; set; } = [];
		public virtual ICollection<CharacterScaffoldPair> ScaffoldPairs { get; set; } = [];
		public virtual ICollection<CharacterShortcut> Shortcuts { get; set; } = [];
		public virtual ICollection<CharacterSkill> Skills { get; set; } = [];
		public virtual ICollection<CharacterSpell> Spells { get; set; } = [];
		public virtual ICollection<CharacterToken> Tokens { get; set; } = [];
		public virtual ICollection<CharacterTag> Tags { get; set; } = [];

		/// <summary>
		///     The client-authored UI settings + select-screen preview, stored verbatim and handed straight back. One
		///     row at most, and absent until the client first uploads one.
		/// </summary>
		public virtual CharacterClientSettings? ClientSettings { get; set; }

		[ConstrainToEnumValues]
		public ConanClass Class { get; set; }

		public DateTimeOffset CreationDate { get; set; }
		public DateTimeOffset? LastConnection { get; set; }

		[ConstrainToEnumValues]
		public GuildRank? GuildRank { get; set; }

		[ConstrainToEnumValues]
		public Race Race { get; set; }

		/// <summary>
		///     Defaults to Male (2): the Sex enum has no 0 member (it is the wire code 2/3), so an unset value would
		///     fail the check - a row created before the editor's ChangeSex (or in tests) still needs a valid sex.
		/// </summary>
		[ConstrainToEnumValues]
		public Sex Sex { get; set; } = Sex.Male;

		/// <summary>
		///     Which of the two swappable weapon loadouts (<c>WeaponSet.Primary</c> = equip slots 6/8, Secondary =
		///     20/21) the character currently has drawn. Toggled by the SwitchWeaponsButton action (0x2E,
		///     capture-confirmed as the set switch) and persisted so both the drawn set and the login-time gear-stat
		///     aggregation (which counts only the active set's weapons, <c>CharacterEquipmentSlot.StatBearing</c>)
		///     survive relog. Default Primary.
		/// </summary>
		public WeaponSet ActiveWeaponSet { get; set; }

		/// <summary>
		///     The vanity ("costume") armour filter (client method ToggleVanityArmorFilter, GameCharAgentProject
		///     0x200B). The per-slot toggles are stored as one bitmask: bit N set = slot N's vanity piece is shown. The
		///     bit index is the toggle's wire <c>slot</c> arg = the base equip slot id, so any slot round-trips by
		///     construction (incl. the still-unidentified 29/30 and the 31 master). Emitted verbatim to the client on
		///     world entry - the FA015807 self-spawn's PlayerSpawnScaffold PreFrame carries it as 4 bytes big-endian -
		///     so the setting survives relog (the client renders the show/hide off it).
		/// </summary>
		/// <remarks>
		///     It lives on the character because it is not a property of any item. Polarity: 1 = enabled = shown
		///     (the wire's toggleState 1 = enabled sets the bit). NecroL80's captured mask 0x000450A4 is bits
		///     2/5/7/12/14/18 = her Head/Belt/Boots/Wrist vanity shown, which is what the client renders.
		///     Capture-confirmed slot mapping: the vanity-head login dump carries bit 2 (Head), vanity-chest carries
		///     bit 5 (Chest); the "global" master toggle is slot 31 -&gt; bit 31. See <c>PlayerSpawnScaffoldRows</c>.
		/// </remarks>
		public uint VanityShownMask { get; set; }

		/// <summary>
		///     The ToggleVanityArmorFilter 'slot' arg / bitmask bit that means the main ("global" hide-all) toggle -
		///     slot 31, capture-confirmed (the "toggle vanity global" toggle carries slot 31).
		/// </summary>
		public const int VanityFilterMainSlot = 31;

		/// <summary>
		///     Apply one ToggleVanityArmorFilter(slot, toggleState): set/clear the slot's bit. toggleState 1 = enabled =
		///     shown, so it sets the bit. The wire <c>slot</c> is the bit index directly (0..31) - no per-slot mapping
		///     needed.
		/// </summary>
		/// <returns>False for an out-of-range slot.</returns>
		public bool SetVanityFilter(uint slot, bool toggleState)
		{
			if (slot > 31)
				return false;

			var bit = 1u << (int)slot;
			if (toggleState)
				VanityShownMask |= bit;
			else
				VanityShownMask &= ~bit;

			return true;
		}

		/// <summary>
		///     True once a bind point has been set (an unbound character recalls to / reports its current map).
		/// </summary>
		[NotMapped]
		public bool HasBind => BindRespawnPointId.HasValue;

		/// <summary>
		///     True while the character is dead and awaiting resurrection. Persisted so a player who logs out dead
		///     returns dead: the world-entry FullPlayerChar snapshot then carries current-health = 0 (the tails stat
		///     block's HealthCurrent stat 0x1B), which is what the retail client reads at SimpleChar construction to
		///     show the release/resurrect window (<c>tools/capture/decode_death_snapshot.py</c>). Set on death
		///     (<c>WorldSimulation.TickPlayerDeaths</c>), cleared on respawn (RespawnPlayer).
		/// </summary>
		public bool IsDead { get; set; }

		/// <summary>
		///     True when this row was built by the capture importer from a retail dump rather than created in-game. It
		///     makes an imported character refreshable: <c>characters --mode overwrite</c> re-imports the rows carrying
		///     it and refuses to touch the rest, so a re-dumped corpus can update an imported bag/worn set/stat sheet
		///     without deleting a player's own character.
		/// </summary>
		/// <remarks>
		///     A marker on the row is the only rule that works. "The name appears in a capture" exempts nothing (the
		///     importer only ever iterates names that do), and the account cannot discriminate either, because
		///     imported characters deliberately sit on the normal play account. Without this column, a refresh that
		///     deletes-and-reinserts on either rule deletes locally created characters.
		/// </remarks>
		public bool ImportedFromCapture { get; set; }

		/// <summary>
		///     Per-character token handed to the client in the small/full char lists, as 32 lowercase hex chars ("N").
		///     Generated once at character creation. Nothing on the server validates it back; the account-level
		///     challenge is the one that gates login.
		/// </summary>
		public Guid AuthChallenge { get; set; }

		[MaxLength(4)]
		public string? Language { get; set; }

		[MaxLength(64)] // INET6_ADDRSTRLEN 46
		public string? LastIpAddress { get; set; }

		[MaxLength(128)]
		public string? Name { get; set; }

		public uint Experience { get; set; }

		/// <summary>
		///     Wallet, in tin (the wire unit and the game's smallest coin; 100 tin = 1 copper, 100 copper = 1 silver,
		///     100 silver = 1 gold). Pushed to the client as a 0x2005 money update [u64 balance][u32 reason] whenever
		///     it changes; see SendUpdateCash.
		/// </summary>
		public ulong Money { get; set; }
		public ushort Level { get; set; }

		/// <summary>
		///     PvE kill/death counters - server-side bookkeeping, never sent. The client keeps two pairs; retail's live
		///     level-80 StatHolder settles which is which: <c>PvPTotalKills</c> 0x290 = 239 and <c>PvPTotalDeaths</c>
		///     0x292 = 521 sit in a contiguous PvP/minigame career block with <c>CaptureSkullWins</c> 0x29C = 13 and
		///     <c>CaptureSkullLosses</c> 0x29D = 10 beside them - and dying more than twice per kill is a PvP record,
		///     not the record of a character who levelled to 80 on mobs.
		/// </summary>
		/// <remarks>
		///     <c>NPCKills</c> (0x298) and <c>NPCDeaths</c> (0x299) are the only two ids of that 0x290-0x29F run absent
		///     from the live holder's 240 stats. The client names them; retail does not populate them. So these two
		///     columns must not be sent - see <c>SnapshotStatSheet</c>. Incremented where the server already knows:
		///     CombatService on a kill, WorldSimulation when the player dies - both PvE-only paths, which is why they
		///     must not feed the PvP slots.
		/// </remarks>
		public uint NpcKills { get; set; }
		public uint NpcDeaths { get; set; }

		/// <summary>
		///     The two alternate-advancement xp pools - the values the client reads as stats <c>AAPvEXP</c> (1000007)
		///     and <c>AAPvPXP</c> (1000006), and divides by <c>AlternateAdvancement_XPCap</c> = 1,000,000 to render the
		///     "next give-away" percentage. Each is a running total toward that cap; reaching it pays tokens and the
		///     remainder carries over (<c>AlternateAdvancementRules.Drain</c>).
		/// </summary>
		/// <remarks>
		///     Columns rather than <see cref="CharacterStat" /> rows: CharacterStat is the FullChar snapshot's stat
		///     sheet, keyed by ordinal within the two captured stat lists, and neither list carries an AA pool - retail
		///     pushes them as standalone StatIIRs instead. Storing them there would put a value in a slot the snapshot
		///     then sends.
		/// </remarks>
		public uint AaPvEXp { get; set; }
		public uint AaPvPXp { get; set; }

		/// <summary>
		///     The PvP career pair the snapshot's 0x290/0x292 carry. Nothing increments them - the server has no PvP
		///     kill path - and 0 is what retail sends for a character with no PvP record, so they are correct at rest
		///     rather than merely unimplemented.
		/// </summary>
		public uint PvpKills { get; set; }
		public uint PvpDeaths { get; set; }

		/// <summary>
		///     The PvP murder points pool, in wire units - the value that goes on stat 0x449 ("Murder Points", named by
		///     the client's own LDB string table). Retail put 3500 on it for a single player kill, and the player read
		///     that off the UI as "36", so the display divides by roughly 100 (see
		///     <c>CriminalStatus.PointsPerPlayerKill</c>). Persisted because it is what the criminal ladder is scored
		///     from and it must survive relog.
		/// </summary>
		public uint MurderPoints { get; set; }

		/// <summary>
		///     Stat <c>0x0A9</c>. The client names it twice, and the two disagree:
		///     <code>
		///     the exe's descriptor registry   HeroicMode   FUN_01db2740 pairs push "HeroicMode" (0x01f6c92c)
		///                                                  with MOV [0x029959c0], 0xa9 on the next instruction
		///     LDB category 615 (and 633)      ChosenGod    the display name table
		///     </code>
		///     We take the exe's: it is compiled with this build, it names ids the LDB does not name at all (the whole
		///     1000000+ range), and <c>player_stats.heroic_mode</c> is a real field of the client's own protobuf
		///     schema while nothing named for a deity is. A localisation string is exactly what goes stale when a stat
		///     slot is repurposed. Age of Conan has no player-choosable religion (deities are lore plus the three
		///     priest classes' patron gods), and the one captured character carrying <c>1</c> is a Necromancer. This
		///     is a judgment between two client sources, not a proof - <c>--report ldb-stat-names</c> prints the
		///     disagreement (<c>0x00A9 HeroicMode != 'ChosenGod'</c>) and 171 more like it.
		/// </summary>
		/// <remarks>
		///     Persisted because it varies per character: 183 of the 184 captured snapshots carry 0 and one carries 1,
		///     so a fixed value is a character wearing someone else's number. Still an opaque number on our side: the
		///     client stores this stat and never reads it back - <c>ConstScan</c> finds exactly two uses of
		///     <c>0xA9</c> as a stat id in the whole image, both writes, no <c>GetStat(0xA9)</c>, and the one class
		///     giving it a dedicated slot (<c>FUN_00843d10</c>/<c>FUN_00843d40</c>, field <c>+0x244</c>) only mirrors
		///     it. So no in-client check can confirm what a given value selects.
		/// </remarks>
		public uint HeroicMode { get; set; }

		/// <summary>
		///     When the character stops being a criminal, or null when it is not one. A timestamp rather than a bool
		///     because retail's is a timed debuff (the captured "Criminal" spell 3827287 runs 300 s) and it persists: in
		///     the annotated capture the debuff outlived a zone change into another instance, so it cannot live on the
		///     session. <see cref="IsCriminal" /> is the question every consumer actually asks.
		/// </summary>
		/// <remarks>
		///     DateTimeOffset, never DateTime: an instant stored without its offset is ambiguous the moment the
		///     process, the host or the deployment changes timezone, and a DateTime round-trips through SQLite with its
		///     Kind silently reset to Unspecified - so a UTC value read back compares wrong against a local <c>now</c>.
		///     Every timestamp column in this model uses DateTimeOffset for that reason.
		/// </remarks>
		public DateTimeOffset? CriminalUntil { get; set; }

		/// <summary>
		///     True while the criminal debuff is running. Criminals are refused by normal vendors and by the wagoneer /
		///     ship-captain travel NPCs - that is not our rule, it is what the client's own spell description says
		///     (<c>CriminalStatus</c>). Smugglers at outlaw camps still trade.
		/// </summary>
		[NotMapped]
		public bool IsCriminal => CriminalUntil is { } until && until > DateTimeOffset.UtcNow;

		/// <summary>
		///     Which Tortage tutorial hints this character has already been shown, as a bitmask of the step ids the
		///     client reports through GameCharAgent.SetTutorialStats. A set and not a "current step": across the
		///     captures the values are not monotone and each session reports only the hints newly seen that session -
		///     one character sends {45,15,16,46}, then {29,30,31,13}, then {10,28} over three logins - so the client is
		///     naming individual hints as they fire, not reporting how far it has got. All 22 observed ids are in
		///     0..47, which is why one u64 holds the whole set.
		/// </summary>
		/// <remarks>
		///     Write-only, deliberately. The client's method catalog has no s2c counterpart to SetTutorialStats, so
		///     there is no way to hand this back - the client keeps its own copy and a fresh install starts the
		///     tutorial over regardless of what is stored. Persisted because it is real per-character state the client
		///     volunteers and it is cheap to keep.
		/// </remarks>
		public ulong TutorialStepsSeen { get; set; }

		public override string ToString()
		{
			return $"{base.ToString()}: {Name}";
		}

		/// <summary>
		///     The vitals ceiling for this character's Class/Level and attributes. Computed, never stored: it is a pure
		///     function of other state, and a persisted copy goes stale on every path that changes the level without
		///     refreshing it (the next login then re-seeds current health from the stale ceiling).
		/// </summary>
		/// <remarks>
		///     The attributes are a required argument rather than something read off the character, because they are
		///     not on it: an attribute score is the class/level base (a DB table) plus worn gear, and both live above
		///     this type. A caller with no character state to hand - a golden test, a create-flow build - passes
		///     <c>VitalAttributes.LevelOneBase</c>, which is the real level-1 row and not a stand-in. What comes out is
		///     the number the client draws, minus the flat gear/feat pool bonuses layered on by
		///     <c>EntityVitals.FromCharacter</c>. See <see cref="CharacterMaxStats.For" />.
		/// </remarks>
		public CharacterMaxStats MaxVitals(VitalAttributes attributes, IReadOnlyDictionary<CharacterStatId, int>? classAttributeBase = null)
		{
			return CharacterMaxStats.For(Class, Level, attributes, classAttributeBase);
		}
	}
}
