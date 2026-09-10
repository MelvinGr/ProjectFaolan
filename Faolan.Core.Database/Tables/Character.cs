using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// Character names are unique PER REALM, not globally (#324): realm Crom and realm Fury may each have a "Bob".
	[Index(nameof(RealmId), nameof(Name), IsUnique = true)]
	public class Character : Base3DEntity
	{
		public uint AccountId { get; set; }

		[ForeignKey(nameof(AccountId))]
		public virtual Account Account { get; set; } = null!;

		// Bind / recall point: the resurrection point the Totem of Origin (and, later, death-recall) returns the
		// character to. A reference to a RespawnPoint the player has DISCOVERED (CharacterRespawnPoint) - set by a
		// wayfarer NPC (today: the .bind GM command). It answers the client's RequestBindLocation poll (the Totem
		// destination playfield) and resolves to the point's map+position on recall. Null = UNBOUND (falls back to the
		// character's current map). See issue #64.
		public uint? BindRespawnPointId { get; set; }

		[ForeignKey(nameof(BindRespawnPointId))]
		public virtual RespawnPoint? BindRespawnPoint { get; set; }

		// Guild membership; null = guildless. Rank 0 = fresh member (retail's invite-accept record), higher =
		// promoted; the guild creator gets GuildLeaderRank. See tools/capture/decode_guild.py for the wire records.
		public uint? GuildId { get; set; }

		[ForeignKey(nameof(GuildId))]
		public virtual Guild? Guild { get; set; }

		// Required: a character always lives on a map; Tortage Beach is the universal fallback world.
		public uint MapId { get; set; } = Map.TortageBeachId;

		// #146: where a PvP-minigame teleport took this character FROM. Written when the match countdown moves them
		// in, cleared by the live leave's teleport-back and by the login redirect that consumes it. Non-null at login
		// means the character last saved INSIDE a match that no longer runs (a mid-match disconnect, a server
		// restart) - the world entry then goes here instead of stranding them on the minigame map.
		public uint? PvpReturnMapId { get; set; }

		public float PvpReturnX { get; set; }

		public float PvpReturnY { get; set; }

		public float PvpReturnZ { get; set; }

		[ForeignKey(nameof(MapId))]
		public virtual Map Map { get; set; } = null!;

		// The mount the character is currently riding (a Mounts row id; null = on foot). Persisted so the mount
		// survives a relog and a trip through a no-mount area: it is set when the player summons a mount and cleared
		// on a voluntary dismount, but NOT on the auto-dismount the client does when entering a no-mount playfield -
		// so re-entering a mount-allowed area (or logging back in there) re-seats the same mount. Re-applied on world
		// entry only when the destination map allows mounts (Map.PlayfieldFlags mount bit).
		public uint? MountId { get; set; }

		// Required: set on creation (CreateCharacter) and by the capture importer.
		public uint RealmId { get; set; }

		[ForeignKey(nameof(RealmId))]
		public virtual Realm Realm { get; set; } = null!;

		public virtual CharacterBody? Body { get; set; }

		// The appearance resources chosen in the create editor (hair/beard mesh, hair/eye/skin color, tattoos,
		// scars), in editor send order. Resolved via MorphTarget into the spawn scaffold's weighted entries - which,
		// with Body's morph sliders, is the WHOLE of the spawn's appearance entry table. That table used to be stored
		// alongside these rows as CharacterAttributes and refreshed from them on every edit path; it is derived at
		// send time now (#483, PlayerSpawnScaffoldRows.BuildAppearanceEntries).
		public virtual ICollection<CharacterBodyResource> BodyResources { get; set; } = [];
		public virtual ICollection<CharacterBuff> Buffs { get; set; } = [];

		// The unlocked FEAT (talent-tree) ids - the spawn's pre-IIR feat list, distinct from Skills (the post-IIR
		// ability scaffold). See CharacterFeat + docs/subsystems/feats.md.
		public virtual ICollection<CharacterFeat> Feats { get; set; } = [];

		// The character's saved feat SPECS (#474). One unnamed spec is the normal case; a feat belongs to exactly one
		// of these via CharacterFeat.SpecIndex.
		public virtual ICollection<CharacterFeatSpec> FeatSpecs { get; set; } = [];

		// EVERY item the character holds - worn (container Equipped, slot = the equip slot id) and carried
		// (bag/resource/quest) alike, one row per item rather than a wide worn row plus an address-keyed carried table
		// (#750). Drives the FullChar inventory snapshot AND the spawn's visible gear. See [[ItemInstance]] /
		// docs/re/player-spawn-generation.md.
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

		// The client-authored UI settings + select-screen preview, stored verbatim and handed straight back (#611).
		// One row at most, and absent until the client first uploads one.
		public virtual CharacterClientSettings? ClientSettings { get; set; }

		[ConstrainToEnumValues]
		public ConanClass Class { get; set; }

		public DateTimeOffset CreationDate { get; set; }
		public DateTimeOffset? LastConnection { get; set; }

		[ConstrainToEnumValues]
		public GuildRank? GuildRank { get; set; }

		[ConstrainToEnumValues]
		public Race Race { get; set; }

		// Defaults to Male (2): the Sex enum has no 0 member (it's the wire code 2/3), so an unset value would fail
		// the CHECK - a row created before the editor's ChangeSex (or in tests) still needs a valid sex.
		[ConstrainToEnumValues]
		public Sex Sex { get; set; } = Sex.Male;

		// #400: which of the two swappable weapon LOADOUTS (WeaponSet.Primary = equip slots 6/8, Secondary = 20/21)
		// the character currently has drawn. Toggled by the SwitchWeaponsButton action (0x2E, capture-confirmed as the
		// SET switch) and persisted so BOTH the drawn set and the login-time gear-stat aggregation (which counts only
		// the ACTIVE set's weapons, CharacterEquipmentSlot.StatBearing) survive relog. Default Primary.
		public WeaponSet ActiveWeaponSet { get; set; }

		// --- vanity ("costume") armour filter (client method ToggleVanityArmorFilter, GameCharAgentProject 0x200B).
		// The per-slot toggles are stored as ONE bitmask: bit N SET = slot N's vanity piece is SHOWN. The bit index IS
		// the toggle's wire `slot` arg = the BASE equip slot id, so any slot round-trips by construction (incl. the
		// still-unidentified 29/30 and the 31 master). Emitted VERBATIM to the client on world entry - the FA015807
		// self-spawn's PlayerSpawnScaffold PreFrame carries it as 4 bytes big-endian - so the setting survives relog
		// (the client renders the show/hide off it).
		//
		// It lives on the CHARACTER because it is not a property of any item: it rode the old wide CharacterEquipment
		// row purely because that row was "the character's gear", and when that row became one instance per worn item
		// (#750) the mask had nowhere item-shaped to go.
		//
		// ⚠️ POLARITY: 1 = enabled = SHOWN. This was modelled backwards and called VanityHiddenMask, which read as the
		// exact opposite of what it holds, and every comment describing it inherited the inversion. The code was
		// always right (`if (toggleState) mask |= bit`, and the wire's toggleState 1 = enabled); only the NAME and the
		// prose were wrong. NecroL80's captured mask 0x000450A4 is bits 2/5/7/12/14/18 = her Head/Belt/Boots/Wrist
		// vanity SHOWN, which is what the client actually renders - under the old "hidden" reading four of her five
		// vanity pieces would have been hidden, which they visibly are not.
		//
		// Capture-confirmed slot mapping: the vanity-head login dump carries bit 2 (Head), vanity-chest carries bit 5
		// (Chest); the "global" master toggle is slot 31 -> bit 31. See PlayerSpawnScaffoldRows (#336).
		public uint VanityShownMask { get; set; }

		// The ToggleVanityArmorFilter 'slot' arg / bitmask bit that means the MAIN ("global" hide-all) toggle - slot
		// 31, capture-confirmed (the "toggle vanity global" toggle carries slot 31).
		public const int VanityFilterMainSlot = 31;

		// Apply one ToggleVanityArmorFilter(slot, toggleState): set/clear the slot's bit. toggleState 1 = enabled =
		// SHOWN, so it sets the bit. The wire `slot` is the bit index directly (0..31) - no per-slot mapping needed.
		// Returns false for an out-of-range slot.
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

		// True once a bind point has been set (an unbound character recalls to / reports its current map).
		[NotMapped]
		public bool HasBind => BindRespawnPointId.HasValue;

		// True while the character is DEAD and awaiting resurrection (#214). Persisted so a player who logs out
		// dead returns dead: the world-entry FullPlayerChar snapshot then carries current-health = 0 (the tails
		// stat block's HealthCurrent stat 0x1B), which is what the retail client reads at SimpleChar construction
		// to show the release/resurrect window (tools/capture/decode_death_snapshot.py). Set on death
		// (WorldSimulation.TickPlayerDeaths), cleared on respawn (RespawnPlayer).
		public bool IsDead { get; set; }

		// True when this row was built by the CAPTURE IMPORTER from a retail dump rather than created in-game
		// (#753). It exists to make an imported character REFRESHABLE: `characters --mode overwrite` re-imports the
		// rows carrying it and refuses to touch the rest, so a re-dumped corpus can update an imported bag/worn
		// set/stat sheet without deleting a player's own character.
		//
		// ⚠️ A marker on the row is the only rule that works, and both of the obvious alternatives were tried and
		// fail: "the name appears in a capture" exempts nothing (the importer only ever iterates names that do),
		// and the ACCOUNT cannot discriminate either, because imported characters deliberately sit on the normal
		// play account - the separate "captured" account was removed in 2026-07 for buying nothing. Without this
		// column, a refresh that deletes-and-reinserts on either rule deletes locally created characters, which is
		// exactly how `Adada` was lost.
		public bool ImportedFromCapture { get; set; }

		// Per-character token handed to the client in the small/full char lists, as 32 lowercase hex chars ("N").
		// Generated once at character creation. NOTE: nothing on the server validates it back yet - see the Gitea
		// follow-up; the account-level challenge is the one that actually gates login.
		public Guid AuthChallenge { get; set; }

		[MaxLength(4)]
		public string? Language { get; set; }

		[MaxLength(64)] // INET6_ADDRSTRLEN 46
		public string? LastIpAddress { get; set; }

		[MaxLength(128)]
		public string? Name { get; set; }

		public uint Experience { get; set; }

		// Wallet, in tin (the wire unit and the game's smallest coin; 100 tin = 1 copper, 100 copper = 1 silver,
		// 100 silver = 1 gold). Pushed to the client as a 0x2005 money update [u64 balance][u32 reason] whenever
		// it changes; see SendUpdateCash.
		public ulong Money { get; set; }
		public ushort Level { get; set; }

		// TWO pairs, because the client keeps two (#808). Retail's live level-80 StatHolder settles which is which:
		// `PvPTotalKills` 0x290 = 239 and `PvPTotalDeaths` 0x292 = 521 sit in a contiguous PvP/minigame career block
		// with `CaptureSkullWins` 0x29C = 13 and `CaptureSkullLosses` 0x29D = 10 beside them - and dying more than
		// twice per kill is a PvP record, not the record of a character who levelled to 80 on mobs.
		//
		// ⚠️ `NPCKills` (0x298) and `NPCDeaths` (0x299) are the ONLY two ids of that 0x290-0x29F run ABSENT from the
		// live holder's 240 stats. The client names them; retail does not populate them. So these two columns are
		// server-side bookkeeping and must NOT be sent - see SnapshotStatSheet.
		//
		// Incremented where the server already knows: CombatService on a kill, WorldSimulation when the player dies.
		// Both of those are PvE-only paths today, which is exactly why they used to feed the PvP slots wrongly.
		public uint NpcKills { get; set; }
		public uint NpcDeaths { get; set; }

		// The two ALTERNATE-ADVANCEMENT xp POOLS (#150) - the values the client reads as stats `AAPvEXP` (1000007)
		// and `AAPvPXP` (1000006), and divides by `AlternateAdvancement_XPCap` = 1,000,000 to render the "next
		// give-away" percentage. Each is a running total toward that cap; reaching it pays tokens and the remainder
		// carries over (AlternateAdvancementRules.Drain).
		//
		// ⚠️ Columns rather than CharacterStat rows: CharacterStat is the FullChar snapshot's stat SHEET, keyed by
		// ordinal within the two captured stat lists, and neither list carries an AA pool - retail pushes them as
		// standalone StatIIRs instead. Storing them there would put a value in a slot the snapshot then sends.
		public uint AaPvEXp { get; set; }
		public uint AaPvPXp { get; set; }

		// The PvP CAREER pair the snapshot's 0x290/0x292 carry. Nothing increments them yet - the server has no PvP
		// kill path - and 0 is what retail sends for a character with no PvP record, so they are correct at rest
		// rather than merely unimplemented (#808).
		public uint PvpKills { get; set; }
		public uint PvpDeaths { get; set; }

		// The PvP MURDER POINTS pool (#503), in WIRE units - the value that goes on stat 0x449 ("Murder Points", named
		// by the client's own LDB string table). Retail put 3500 on it for a single player kill, and the player read
		// that off the UI as "36", so the display divides by roughly 100 (see CriminalStatus.PointsPerPlayerKill).
		// Persisted because it is what the criminal ladder is scored from and it must survive relog.
		public uint MurderPoints { get; set; }

		// Stat `0x0A9`. **The client names it TWICE, and the two disagree** (#625):
		//
		//   the EXE's descriptor registry   `HeroicMode`   `FUN_01db2740` pairs `PUSH "HeroicMode"` (0x01f6c92c)
		//                                                  with `MOV [0x029959c0], 0xa9` on the next instruction
		//   LDB category 615 (and 633)      `ChosenGod`    the DISPLAY name table
		//
		// ⭐ **We take the exe's.** It is compiled with this build, it names ids the LDB does not name at all (the
		// whole 1000000+ range), and `player_stats.heroic_mode` is a real field of the client's own protobuf schema
		// while nothing named for a deity is. A localisation string is exactly what goes stale when a stat slot is
		// repurposed, and this reads as one: an early religion idea whose slot became heroic mode, with the LDB text
		// never updated. ⚠️ It is a JUDGMENT between two client sources, not a proof - `--report ldb-stat-names`
		// prints the disagreement (`0x00A9 HeroicMode != 'ChosenGod'`) and 171 more like it.
		//
		// The decisive supporting fact is that the old name had nothing to be about: **Age of Conan has no
		// player-choosable religion.** Deities are lore plus the three PRIEST classes' patron gods. #553 was opened on
		// the premise that "AoC's religion is chosen during the Tortage storyline" - that is CONAN EXILES, a different
		// game - and the one captured character carrying `1` is a NECROMANCER, which is a puzzle under `ChosenGod`
		// and not one under `HeroicMode`.
		//
		// PERSISTED because it VARIES per character and we were sending a constant: 183 of the 184 captured snapshots
		// carry 0 and one carries 1, so a fixed value is a character wearing someone else's number.
		//
		// ⚠️ Still an OPAQUE NUMBER on our side, whatever it is called. The client STORES this stat and never reads it
		// back - `ConstScan` finds exactly two uses of `0xA9` as a stat id in the whole image, both WRITES, no
		// `GetStat(0xA9)`, and the one class giving it a dedicated slot (`FUN_00843d10`/`FUN_00843d40`, field `+0x244`)
		// only mirrors it. So no in-client check can confirm what a given VALUE selects.
		public uint HeroicMode { get; set; }

		// When the character stops being a CRIMINAL, or null when it is not one. A timestamp rather than a bool
		// because retail's is a timed debuff (the captured "Criminal" spell 3827287 runs 300 s) and it PERSISTS: in the
		// annotated capture the debuff outlived a zone change into another instance, so it cannot live on the session.
		// `IsCriminal` is the question every consumer actually asks.
		//
		// DateTimeOffset, never DateTime: an instant stored without its offset is ambiguous the moment the process,
		// the host or the deployment changes timezone, and a DateTime round-trips through SQLite with its Kind
		// silently reset to Unspecified - so a UTC value read back compares wrong against a local `now`. Every
		// timestamp column in this model uses DateTimeOffset for that reason.
		public DateTimeOffset? CriminalUntil { get; set; }

		// True while the criminal debuff is running. Criminals are refused by normal vendors and by the wagoneer /
		// ship-captain travel NPCs - that is not our rule, it is what the client's own spell description says
		// (CriminalStatus). Smugglers at outlaw camps still trade.
		[NotMapped]
		public bool IsCriminal => CriminalUntil is { } until && until > DateTimeOffset.UtcNow;

		// Which Tortage tutorial hints this character has already been shown, as a BITMASK of the step ids the client
		// reports through GameCharAgent.SetTutorialStats (#461). A set and not a "current step": across the captures
		// the values are not monotone and each session reports only the hints newly seen that session - Faolanx sends
		// {45,15,16,46}, then {29,30,31,13}, then {10,28} over three logins - so the client is naming individual hints
		// as they fire, not reporting how far it has got. All 22 observed ids are in 0..47, which is why one u64 holds
		// the whole set.
		//
		// ⚠️ Write-only for now, and deliberately so. The client's method catalog has NO s2c counterpart to
		// SetTutorialStats, so there is no way to hand this back - the client keeps its own copy and a fresh install
		// starts the tutorial over regardless of what we stored. We persist it because it is real per-character state
		// the client volunteers and it is cheap to keep; restoring it needs a feed-back path nobody has found yet.
		public ulong TutorialStepsSeen { get; set; }

		public override string ToString()
		{
			return $"{base.ToString()}: {Name}";
		}

		// The vitals ceiling for this character's Class/Level and ATTRIBUTES. COMPUTED, never stored (#578).
		//
		// It used to be a persisted `CharacterMaxStats` row, and storing a value that is a pure function of other
		// state is what made the bug: only the `.level` GM command refreshed it, so a combat level-up wrote the new
		// Level and left the row at its creation value. The next login re-seeded current health from that stale
		// ceiling and the player came back at a fraction of their bar - a level-2 necromancer reading 89/955. There is
		// nothing to keep in sync now, so it cannot go stale again.
		//
		// The attributes are a REQUIRED argument rather than something read off the character, because they are not on
		// it: an attribute score is the class/level base (#444, a DB table) plus worn gear, and both live above this
		// type. A caller with no character state to hand - a golden test, a create-flow build - passes
		// VitalAttributes.LevelOneBase, which is the real level-1 row and not a stand-in.
		//
		// What comes out IS the number the client draws, minus the flat gear/feat pool bonuses layered on by
		// EntityVitals.FromCharacter. See CharacterMaxStats.For.
		public CharacterMaxStats MaxVitals(VitalAttributes attributes, IReadOnlyDictionary<CharacterStatId, int>? classAttributeBase = null)
		{
			return CharacterMaxStats.For(Class, Level, attributes, classAttributeBase);
		}
	}
}
