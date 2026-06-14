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
		// promoted; the guild creator gets GuildLeaderRank. See Captures/decode_guild.py for the wire records.
		public uint? GuildId { get; set; }
		public byte GuildRank { get; set; }

		public const byte GuildLeaderRank = 6;

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

		// Approximate class/level defaults so a character has sensible vitals until a real stat system exists.
		// Which pools a class HAS is taken from the captured retail characters (memory class_resource_mana_stamina,
		// authoritative): pure-physical classes (melee Soldiers + all Rogues) have Stamina only, pure casters
		// (Mages + ranged Priests) have Mana only, and the three melee-hybrids (Dark Templar / Bear Shaman / Herald
		// of Xotli) have both. The unused pool is zeroed so the client hides that bar. Magnitudes are intentionally
		// rough; combat/levelling will replace these once those systems land.
		public static CharacterMaxStats CreateDefaults(Class @class, ushort level)
		{
			var lvl = level == 0
				? (ushort)1
				: level;
			var (healthPerLevel, manaBase, manaPerLevel, usesStamina, usesMana) = Archetype(@class);

			return new CharacterMaxStats
			{
				Health = (uint)(200 + lvl * healthPerLevel),
				Stamina = usesStamina
					? (uint)(200 + lvl * (healthPerLevel - 6))
					: 0,
				Mana = usesMana
					? (uint)(manaBase + lvl * manaPerLevel)
					: 0
			};
		}

		// (healthPerLevel, manaBase, manaPerLevel, usesStamina, usesMana) by class. The stamina/mana flags match the
		// captured retail CharacterMaxStats (see memory class_resource_mana_stamina).
		private static (int HealthPerLevel, int ManaBase, int ManaPerLevel, bool UsesStamina, bool UsesMana) Archetype(Class @class)
		{
			switch (@class)
			{
				// Melee Soldiers - high health, STAMINA ONLY.
				case Class.Guardian:
				case Class.Conqueror:
					return (60, 0, 0, true, false);

				// Dark Templar - Soldier hybrid, BOTH (unholy magic + melee).
				case Class.DarkTemplar:
					return (60, 50, 1, true, true);

				// Rogues - medium health, STAMINA ONLY (captures show Assassin has no mana).
				case Class.Barbarian:
				case Class.Assassin:
				case Class.Ranger:
					return (47, 0, 0, true, false);

				// Ranged Priests - medium health, MANA ONLY.
				case Class.PriestOfMitra:
				case Class.TempestOfSet:
					return (45, 80, 30, false, true);

				// Bear Shaman - Priest melee hybrid, BOTH.
				case Class.BearShaman:
					return (45, 80, 30, true, true);

				// Herald of Xotli - Mage melee hybrid, BOTH.
				case Class.HeraldOfXotli:
					return (38, 100, 40, true, true);

				// Pure Mages - low health, MANA ONLY.
				case Class.Demonologist:
				case Class.Necromancer:
					return (38, 100, 40, false, true);

				default:
					return (47, 50, 1, true, true);
			}
		}
	}
}
