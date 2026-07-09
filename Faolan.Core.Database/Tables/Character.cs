using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	[Index(nameof(Name), IsUnique = true)]
	public class Character : Base3DEntity
	{
		// The guild-creator's rank on the wire. 4 = leader, proven by the create-guild capture (the member record
		// in DispatchGuildCreated / GuildFullUpdate); the earlier 6 was a blind guess. No rank-comparison logic
		// depends on the exact value, so this only affects the wire member record (now retail-accurate). See #306.
		public const byte GuildLeaderRank = 4;

		public uint AccountId { get; set; }

		[ForeignKey(nameof(AccountId))]
		public virtual Account Account { get; set; }

		// Bind / recall point: the resurrection point the Totem of Origin (and, later, death-recall) returns the
		// character to. A reference to a RespawnPoint the player has DISCOVERED (CharacterRespawnPoint) - set by a
		// wayfarer NPC (today: the .bind GM command). It answers the client's RequestBindLocation poll (the Totem
		// destination playfield) and resolves to the point's map+position on recall. Null = UNBOUND (falls back to the
		// character's current map). See issue #64.
		public uint? BindRespawnPointId { get; set; }

		[ForeignKey(nameof(BindRespawnPointId))]
		public virtual RespawnPoint BindRespawnPoint { get; set; }

		// Guild membership; null = guildless. Rank 0 = fresh member (retail's invite-accept record), higher =
		// promoted; the guild creator gets GuildLeaderRank. See Captures/scripts/decode_guild.py for the wire records.
		public uint? GuildId { get; set; }

		[ForeignKey(nameof(GuildId))]
		public virtual Guild Guild { get; set; }

		// Required: a character always lives on a map; Tortage Beach is the universal fallback world.
		public uint MapId { get; set; } = Map.TortageBeachId;

		[ForeignKey(nameof(MapId))]
		public virtual Map Map { get; set; }

		// Required: set on creation (CreateCharacter) and by the capture importer.
		public uint RealmId { get; set; }

		[ForeignKey(nameof(RealmId))]
		public virtual Realm Realm { get; set; }

		public virtual CharacterBody Body { get; set; }
		public virtual CharacterEquipment Equipment { get; set; }

		// One-to-one max-stat block (Health/Mana/Stamina, player-facing values). Created with class/level
		// defaults on character creation; the ×100 vitals wire scale is applied when sending (CharacterStatBlock).
		public virtual CharacterMaxStats MaxStats { get; set; }

		// The spawn's IIR scaffold, structured so it can be generated instead of template-copied: the uniform
		// entry table (Attributes), the feat/ability ID list (Skills), the two ending pair lists (ScaffoldPairs)
		// (the pre-IIR frame + EntryTableX are reconstructed from Class at spawn time, not stored - they carry no
		// character data). See docs/archive/player-spawn-generation-plan.md + docs/fa015807-spawn-reader.md.
		public virtual ICollection<CharacterAttribute> Attributes { get; set; }

		// The appearance resources chosen in the create editor (hair/beard mesh, hair/eye/skin color, tattoos,
		// scars), in editor send order. Resolved via MorphTarget into the spawn scaffold's weighted entries.
		public virtual ICollection<CharacterBodyResource> BodyResources { get; set; }

		// Non-worn carried items (bag/backpack/quest/resources); worn items are in Equipment. Drives the FullChar
		// inventory snapshot. See [[CharacterInventory]] / docs/archive/player-spawn-generation-plan.md.
		public virtual ICollection<CharacterInventory> Inventory { get; set; }

		// Per-character progression/state collections (inverse sides of the matching child tables, all cascade-delete
		// with the character). Quests/Tags are the quest-state machine, RespawnPoints the resurrection points the
		// character has discovered, Shortcuts the hotbar, Spells the learned spell set re-granted on login.
		public virtual ICollection<CharacterQuest> Quests { get; set; }
		public virtual ICollection<CharacterRespawnPoint> RespawnPoints { get; set; }
		public virtual ICollection<CharacterScaffoldPair> ScaffoldPairs { get; set; }
		public virtual ICollection<CharacterShortcut> Shortcuts { get; set; }
		public virtual ICollection<CharacterSkill> Skills { get; set; }
		public virtual ICollection<CharacterSpell> Spells { get; set; }
		public virtual ICollection<CharacterTag> Tags { get; set; }

		// True once a bind point has been set (an unbound character recalls to / reports its current map).
		[NotMapped]
		public bool HasBind => BindRespawnPointId.HasValue;

		// True while the character is DEAD and awaiting resurrection (#214). Persisted so a player who logs out
		// dead returns dead: the world-entry FullPlayerChar snapshot then carries current-health = 0 (the tails
		// stat block's HealthCurrent stat 0x1B), which is what the retail client reads at SimpleChar construction
		// to show the release/resurrect window (Captures/scripts/decode_death_snapshot.py). Set on death
		// (WorldSimulation.TickPlayerDeaths), cleared on respawn (RespawnPlayer).
		public bool IsDead { get; set; }
		public byte GuildRank { get; set; }

		[ConstrainToEnumValues]
		public Class Class { get; set; }

		public DateTimeOffset CreationDate { get; set; }
		public DateTimeOffset? LastConnection { get; set; }

		[ConstrainToEnumValues]
		public Race Race { get; set; }

		// Defaults to Male (2): the Sex enum has no 0 member (it's the wire code 2/3), so an unset value would fail
		// the CHECK - a row created before the editor's ChangeSex (or in tests) still needs a valid sex.
		[ConstrainToEnumValues]
		public Sex Sex { get; set; } = Sex.Male;

		[MaxLength(32)]
		public string AuthChallenge { get; set; }

		[MaxLength(4)]
		public string Language { get; set; }

		[MaxLength(64)] // INET6_ADDRSTRLEN 46
		public string LastIpAddress { get; set; }

		[MaxLength(128)]
		public string Name { get; set; }

		public uint Experience { get; set; }

		// Wallet, in tin (the wire unit and the game's smallest coin; 100 tin = 1 copper, 100 copper = 1 silver,
		// 100 silver = 1 gold). Pushed to the client as a 0x2005 money update [u64 balance][u32 reason] whenever
		// it changes; see Send0x2005_MoneyUpdate.
		public ulong Money { get; set; }
		public ushort Level { get; set; }

		public override string ToString()
		{
			return $"{base.ToString()}: {Name}";
		}

		//[NotMapped]
		//public string LastConnectionRealmString => DateTimeOffset.FromUnixTimeSeconds(LastConnection).ToString("MM/dd/yyyy HH:mm:ss");

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
