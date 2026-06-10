using System;
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

		// Non-worn carried items (bag/backpack/quest/resources); worn items are in Equipment. Drives the FullChar
		// inventory snapshot. See [[CharacterInventory]] / docs/player-spawn-generation-plan.md.
		public virtual System.Collections.Generic.ICollection<CharacterInventory> Inventory { get; set; }

		// One-to-one max-stat block (Health/Mana/Stamina, player-facing values). Created with class/level
		// defaults on character creation; the ×100 vitals wire scale is applied when sending (CharacterStatBlock).
		public virtual CharacterMaxStats MaxStats { get; set; }

		[ForeignKey(nameof(MapId))]
		public virtual Map Map { get; set; }

		[ForeignKey(nameof(RealmId))]
		public virtual Realm Realm { get; set; }

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

		public uint? MapId { get; set; }

		public uint? RealmId { get; set; }
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
		// Anchored on assassinl23 (Assassin, lvl 23: Health 1270, Mana 74, Stamina 1148) - a rogue archetype check.
		// Values are intentionally rough; combat/levelling will replace these once those systems land.
		public static CharacterMaxStats CreateDefaults(Class @class, ushort level)
		{
			var lvl = level == 0
				? (ushort)1
				: level;
			var (healthPerLevel, manaBase, manaPerLevel) = Archetype(@class);

			return new CharacterMaxStats
			{
				Health = (uint)(200 + lvl * healthPerLevel),
				Stamina = (uint)(200 + lvl * (healthPerLevel - 6)),
				Mana = (uint)(manaBase + lvl * manaPerLevel)
			};
		}

		// (healthPerLevel, manaBase, manaPerLevel) by AoC class archetype.
		private static (int HealthPerLevel, int ManaBase, int ManaPerLevel) Archetype(Class @class)
		{
			switch (@class)
			{
				// Soldiers - high health, little mana.
				case Class.Guardian:
				case Class.Conqueror:
				case Class.DarkTemplar:
					return (60, 50, 1);

				// Rogues - medium health, little mana (Assassin lvl 23 -> ~1281 HP / ~73 mana).
				case Class.Barbarian:
				case Class.Assassin:
				case Class.Ranger:
					return (47, 50, 1);

				// Priests - medium health, moderate mana.
				case Class.PriestOfMitra:
				case Class.TempestOfSet:
				case Class.BearShaman:
					return (45, 80, 30);

				// Mages - low health, high mana.
				case Class.HeraldOfXotli:
				case Class.Demonologist:
				case Class.Necromancer:
					return (38, 100, 40);

				default:
					return (47, 50, 1);
			}
		}
	}
}
