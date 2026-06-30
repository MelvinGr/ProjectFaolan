using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	[Index(nameof(Name), IsUnique = true)]
	public class Character : Base3DEntity
	{
		[ForeignKey(nameof(AccountId))]
		public virtual Account Account { get; set; }

		public virtual CharacterBody Body { get; set; }
		public virtual CharacterEquipment Equipment { get; set; }

		// One-to-one max-stat block (Health/Mana/Stamina, player-facing values). Created with class/level
		// defaults on character creation; the ×100 vitals wire scale is applied when sending (CharacterStatBlock).
		public virtual CharacterMaxStats MaxStats { get; set; }
		public virtual CharacterSpawnFrame SpawnFrame { get; set; }

		// The spawn's IIR scaffold, structured so it can be generated instead of template-copied: the uniform
		// entry table (Attributes), the feat/ability ID list (Skills), the two ending pair lists (ScaffoldPairs)
		// and the raw pre-IIR frame + scalars (SpawnFrame). See docs/archive/player-spawn-generation-plan.md.
		public virtual ICollection<CharacterAttribute> Attributes { get; set; }

		// The appearance resources chosen in the create editor (hair/beard mesh, hair/eye/skin color, tattoos,
		// scars), in editor send order. Resolved via MorphTarget into the spawn scaffold's weighted entries.
		public virtual ICollection<CharacterBodyResource> BodyResources { get; set; }

		// Non-worn carried items (bag/backpack/quest/resources); worn items are in Equipment. Drives the FullChar
		// inventory snapshot. See [[CharacterInventory]] / docs/archive/player-spawn-generation-plan.md.
		public virtual ICollection<CharacterInventory> Inventory { get; set; }
		public virtual ICollection<CharacterScaffoldPair> ScaffoldPairs { get; set; }
		public virtual ICollection<CharacterSkill> Skills { get; set; }

		// Per-character progression/state collections (inverse sides of the matching child tables, all cascade-delete
		// with the character). Quests/Tags are the quest-state machine, RespawnPoints the resurrection points the
		// character has discovered, Shortcuts the hotbar, Spells the learned spell set re-granted on login.
		public virtual ICollection<CharacterQuest> Quests { get; set; }
		public virtual ICollection<CharacterTag> Tags { get; set; }
		public virtual ICollection<CharacterRespawnPoint> RespawnPoints { get; set; }
		public virtual ICollection<CharacterShortcut> Shortcuts { get; set; }
		public virtual ICollection<CharacterSpell> Spells { get; set; }

		[ForeignKey(nameof(MapId))]
		public virtual Map Map { get; set; }

		[ForeignKey(nameof(RealmId))]
		public virtual Realm Realm { get; set; }

		[ForeignKey(nameof(GuildId))]
		public virtual Guild Guild { get; set; }

		public Class Class { get; set; }
		public DateTimeOffset CreationDate { get; set; }

		public DateTimeOffset? LastConnection { get; set; }
		public Race Race { get; set; }
		public Sex Sex { get; set; }

		[MaxLength(32)]
		public string AuthChallenge { get; set; }

		[MaxLength(4)]
		public string Language { get; set; }

		[MaxLength(45)] // INET6_ADDRSTRLEN 46
		public string LastIpAddress { get; set; }

		[MaxLength(128)]
		public string Name { get; set; }

		public uint AccountId { get; set; }
		public uint Experience { get; set; }

		// Required: a character always lives on a map; Tortage Beach is the universal fallback world.
		public uint MapId { get; set; } = Map.TortageBeachId;

		// Required: set on creation (CreateCharacter) and by the capture importer.
		public uint RealmId { get; set; }
		public ushort Level { get; set; }

		// Wallet, in tin (the wire unit and the game's smallest coin; 100 tin = 1 copper, 100 copper = 1 silver,
		// 100 silver = 1 gold). Pushed to the client as a 0x2005 money update [u64 balance][u32 reason] whenever
		// it changes; see Send0x2005_MoneyUpdate.
		public ulong Money { get; set; }

		// Guild membership; null = guildless. Rank 0 = fresh member (retail's invite-accept record), higher =
		// promoted; the guild creator gets GuildLeaderRank. See Captures/scripts/decode_guild.py for the wire records.
		public uint? GuildId { get; set; }
		public byte GuildRank { get; set; }

		public const byte GuildLeaderRank = 6;

		// Bind / recall point: the resurrection point the Totem of Origin (and, later, death-recall) returns the
		// character to. A reference to a RespawnPoint the player has DISCOVERED (CharacterRespawnPoint) - set by a
		// wayfarer NPC (today: the .bind GM command). It answers the client's RequestBindLocation poll (the Totem
		// destination playfield) and resolves to the point's map+position on recall. Null = UNBOUND (falls back to the
		// character's current map). See issue #64.
		public uint? BindRespawnPointId { get; set; }

		[ForeignKey(nameof(BindRespawnPointId))]
		public virtual RespawnPoint BindRespawnPoint { get; set; }

		// True once a bind point has been set (an unbound character recalls to / reports its current map).
		[NotMapped]
		public bool HasBind => BindRespawnPointId.HasValue;

		// True while the character is DEAD and awaiting resurrection (#214). Persisted so a player who logs out
		// dead returns dead: the world-entry FullPlayerChar snapshot then carries current-health = 0 (the tails
		// stat block's HealthCurrent stat 0x1B), which is what the retail client reads at SimpleChar construction
		// to show the release/resurrect window (Captures/scripts/decode_death_snapshot.py). Set on death
		// (WorldSimulation.TickPlayerDeaths), cleared on respawn (RespawnPlayer).
		public bool IsDead { get; set; }

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

	public class CharacterMaxStats : BaseEntity
	{
		public virtual Character Character { get; set; }
		public uint CharacterId { get; set; }

		// Player-facing values (what the character sheet shows), e.g. Health 1270. The on-wire ×100 vitals scale
		// is applied at send time, not stored.
		public uint Health { get; set; }
		public uint Mana { get; set; }
		public uint Stamina { get; set; }

		// Class/level vitals. The LEVEL-1 values are anchored to the retail character sheet (read off the live
		// client 2026-06-17, cross-checked against the world-entry FullChar snapshot stat block - see
		// docs/handoff-2026-06-17-perclass-snapshot.md and #242): all 12 classes are now retail-anchored at L1 -
		// Guardian 130/0/179, Conqueror 168/0/178, Dark Templar 128/281/178, Assassin 98/0/236, Barbarian
		// 118/0/178, Ranger 133/0/213, Bear Shaman 108/171/178, Priest of Mitra 88/171/0, Tempest of Set 88/171/0,
		// Necromancer 89/246/0, Demonologist 89/246/0, Herald of Xotli 89/246/121 (HP/Mana/Stamina). Which pools a
		// class HAS matches memory class_resource_mana_stamina (a 0 anchor zeroes the pool so the client hides that
		// bar). PER-LEVEL growth is still rough (no multi-level captures yet); only L1 is authoritative.
		public static CharacterMaxStats CreateDefaults(Class @class, ushort level)
		{
			var lvl = level == 0
				? (ushort)1
				: level;
			var extra = lvl - 1;
			var (hpL1, hpGrow, manaL1, manaGrow, staminaL1, staminaGrow, _) = Archetype(@class);

			return new CharacterMaxStats
			{
				Health = (uint)(hpL1 + extra * hpGrow),
				Mana = manaL1 == 0
					? 0
					: (uint)(manaL1 + extra * manaGrow),
				Stamina = staminaL1 == 0
					? 0
					: (uint)(staminaL1 + extra * staminaGrow)
			};
		}

		// The FullChar snapshot stat block (TailsPostAccessBlock) carries, besides the DISPLAYED max for each pool, a
		// slightly-lower "base/secondary" value the client reads into a parallel slot. From the four archetypes' retail
		// LOGIN snapshots (decode_statblock.py over Necro/Guardian/Assassin/Bear-Shaman): mana base = displayed-6,
		// stamina base = displayed-6, HP base = displayed - a per-class L1 delta (Necro 9, Guardian 10, Assassin/
		// BearShaman 8). Authoring the WHOLE per-class block (both the displayed AND base slots) keeps it internally
		// consistent - patching only the displayed slot and leaving the base at the Strie template's Rogue value was the
		// suspected #4 (wrong mana) / "gear vanished" inconsistency (docs/handoff-2026-06-17-perclass-snapshot.md).
		// Returned in display units (the block scales ×100 at write time). #186/#4.
		public (uint Hp, uint HpBase, uint Mana, uint ManaBase, uint Stamina, uint StaminaBase) SnapshotVitals(Class @class)
		{
			var a = Archetype(@class);
			var hpDelta = (uint)(a.HpL1 - a.HpBaseL1);
			var hpBase = Health > hpDelta ? Health - hpDelta : Health;
			var manaBase = Mana > 6 ? Mana - 6 : 0;       // 0-pool classes stay 0; mana base is 6 below the displayed max
			var staminaBase = Stamina > 6 ? Stamina - 6 : 0;
			return (Health, hpBase, Mana, manaBase, Stamina, staminaBase);
		}

		// L1-anchored vitals by class: (hpL1, hpGrow, manaL1, manaGrow, staminaL1, staminaGrow, hpBaseL1). A 0 for
		// manaL1 / staminaL1 means the class does not have that pool. The L1 values for Soldier/Rogue/Mage/Bear-Shaman
		// (incl. hpBaseL1 = the snapshot's lower HP-base slot) are from retail captures; the rest are estimates (marked).
		// Growth is a placeholder until levelling lands.
		private static (int HpL1, int HpGrow, int ManaL1, int ManaGrow, int StaminaL1, int StaminaGrow, int HpBaseL1) Archetype(Class @class)
		{
			switch (@class)
			{
				// Melee Soldiers - STAMINA ONLY. Retail L1 (login snapshots, #242): Guardian HP 130 (base 120) /
				// Stam 179, Conqueror HP 168 (base 160) / Stam 178.
				case Class.Guardian:
					return (130, 60, 0, 0, 179, 54, 120);

				case Class.Conqueror:
					return (168, 60, 0, 0, 178, 54, 160);

				// Dark Templar - Soldier hybrid, BOTH. Retail L1 (login snapshot, #242): HP 128 (base 120),
				// Mana 281, Stamina 178.
				case Class.DarkTemplar:
					return (128, 60, 281, 30, 178, 54, 120);

				// Rogues - STAMINA ONLY. Each class differs at L1 (login snapshots, #242): Assassin HP 98 (base 90) /
				// Stam 236, Barbarian HP 118 (base 110) / Stam 178, Ranger HP 133 (base 120) / Stam 213. Growth is
				// fitted to the Assassin's TWO captured points (L1 Strie 98/236, L23 AssassinL23 1270/736 -> ~53 HP /
				// ~23 Stam per level) and reused for all three until per-class multi-level captures land.
				case Class.Assassin:
					return (98, 53, 0, 0, 236, 23, 90);

				case Class.Barbarian:
					return (118, 53, 0, 0, 178, 23, 110);

				case Class.Ranger:
					return (133, 53, 0, 0, 213, 23, 120);

				// Ranged Priests - MANA ONLY. Retail L1 (login snapshots, #242): Priest of Mitra and Tempest of Set
				// both HP 88 (base 80) / Mana 171 / no stamina pool.
				case Class.PriestOfMitra:
				case Class.TempestOfSet:
					return (88, 45, 171, 30, 0, 0, 80);

				// Bear Shaman - Priest melee hybrid, BOTH. Retail L1: HP 108 (base 100), Mana 171, Stamina 178.
				case Class.BearShaman:
					return (108, 45, 171, 30, 178, 39, 100);

				// Herald of Xotli - Mage melee hybrid, BOTH. Retail L1 (login snapshot, #242): HP 89 (base 80),
				// Mana 246, Stamina 121.
				case Class.HeraldOfXotli:
					return (89, 38, 246, 40, 121, 32, 80);

				// Pure Mages - MANA ONLY. Retail L1 (login snapshots, #242): Necromancer and Demonologist both HP 89
				// (base 80) / Mana 246 / no stamina pool.
				case Class.Demonologist:
				case Class.Necromancer:
					return (89, 38, 246, 40, 0, 0, 80);

				default:
					return (98, 47, 150, 30, 150, 41, 90);
			}
		}
	}
}
