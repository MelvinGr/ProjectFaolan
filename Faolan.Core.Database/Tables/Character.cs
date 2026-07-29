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

		[ForeignKey(nameof(MapId))]
		public virtual Map Map { get; set; } = null!;

		// The mount the character is currently riding (a MountCatalog mount id; null = on foot). Persisted so the mount
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
		public virtual CharacterEquipment? Equipment { get; set; }

		// One-to-one max-stat block (Health/Mana/Stamina, player-facing values). Created with class/level
		// defaults on character creation; the ×100 vitals wire scale is applied when sending (CharacterStatBlock).
		public virtual CharacterMaxStats? MaxStats { get; set; }

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

		// Non-worn carried items (bag/backpack/quest/resources); worn items are in Equipment. Drives the FullChar
		// inventory snapshot. See [[CharacterInventory]] / docs/re/player-spawn-generation.md.
		public virtual ICollection<CharacterInventory> Inventory { get; set; } = [];

		// Per-character progression/state collections (inverse sides of the matching child tables, all cascade-delete
		// with the character). Quests/Tags are the quest-state machine, RespawnPoints the resurrection points the
		// character has discovered, Shortcuts the hotbar, Spells the learned spell set re-granted on login.
		public virtual ICollection<CharacterQuest> Quests { get; set; } = [];
		public virtual ICollection<CharacterRespawnPoint> RespawnPoints { get; set; } = [];
		public virtual ICollection<CharacterScaffoldPair> ScaffoldPairs { get; set; } = [];
		public virtual ICollection<CharacterShortcut> Shortcuts { get; set; } = [];
		public virtual ICollection<CharacterSkill> Skills { get; set; } = [];
		public virtual ICollection<CharacterSpell> Spells { get; set; } = [];
		public virtual ICollection<CharacterTag> Tags { get; set; } = [];

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
		// the ACTIVE set's weapons, CharacterEquipment.StatBearingItemIds) survive relog. Default Primary.
		public WeaponSet ActiveWeaponSet { get; set; }

		// True once a bind point has been set (an unbound character recalls to / reports its current map).
		[NotMapped]
		public bool HasBind => BindRespawnPointId.HasValue;

		// True while the character is DEAD and awaiting resurrection (#214). Persisted so a player who logs out
		// dead returns dead: the world-entry FullPlayerChar snapshot then carries current-health = 0 (the tails
		// stat block's HealthCurrent stat 0x1B), which is what the retail client reads at SimpleChar construction
		// to show the release/resurrect window (tools/capture/decode_death_snapshot.py). Set on death
		// (WorldSimulation.TickPlayerDeaths), cleared on respawn (RespawnPlayer).
		public bool IsDead { get; set; }

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

		// Kill/death counters, shown on the character sheet (snapshot stats Kills 0x2A2 / Deaths 0x292). They vary per
		// character across the captured corpus, so carrying them at the captured character's value shipped someone
		// else's record to everyone (#447). Incremented where the server already knows: CombatService on a kill,
		// WorldSimulation when the player dies.
		public uint Kills { get; set; }
		public uint Deaths { get; set; }

		// The PvP MURDER POINTS pool (#503), in WIRE units - the value that goes on stat 0x449 ("Murder Points", named
		// by the client's own LDB string table). Retail put 3500 on it for a single player kill, and the player read
		// that off the UI as "36", so the display divides by roughly 100 (see CriminalStatus.PointsPerPlayerKill).
		// Persisted because it is what the criminal ladder is scored from and it must survive relog.
		public uint MurderPoints { get; set; }

		// When the character stops being a CRIMINAL, or null when it is not one. A timestamp rather than a bool
		// because retail's is a timed debuff (the captured "Criminal" spell 3827287 runs 300 s) and it PERSISTS: in the
		// annotated capture the debuff outlived a zone change into another instance, so it cannot live on the session.
		// `IsCriminal` is the question every consumer actually asks.
		public DateTime? CriminalUntil { get; set; }

		// True while the criminal debuff is running. Criminals are refused by normal vendors and by the wagoneer /
		// ship-captain travel NPCs - that is not our rule, it is what the client's own spell description says
		// (CriminalStatus). Smugglers at outlaw camps still trade.
		[NotMapped]
		public bool IsCriminal => CriminalUntil is { } until && until > DateTime.UtcNow;

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

		// Recomputes MaxStats from the current Class/Level. Updates the existing row in place when present (so EF
		// keeps tracking the same row instead of orphaning/duplicating it) and creates it otherwise. Call when the
		// class or level changes - e.g. once the create-editor commits the chosen class.
		public void RegenerateMaxStats()
		{
			var defaults = CharacterMaxStats.CreateDefaults(Class, Level);
			if (MaxStats == null)
			{
				MaxStats = defaults;
				return;
			}

			MaxStats.Health = defaults.Health;
			MaxStats.Mana = defaults.Mana;
			MaxStats.Stamina = defaults.Stamina;
		}
	}
}
