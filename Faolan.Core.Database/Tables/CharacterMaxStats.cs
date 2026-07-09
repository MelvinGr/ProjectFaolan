using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	[PrimaryKey(nameof(CharacterId))]
	public class CharacterMaxStats
	{
		public uint CharacterId { get; set; }
		public virtual Character Character { get; set; }

		// Player-facing values (what the character sheet shows), e.g. Health 1270. The on-wire ×100 vitals scale
		// is applied at send time, not stored.
		public uint Health { get; set; }
		public uint Mana { get; set; }
		public uint Stamina { get; set; }

		// The FullChar snapshot stat block (TailsPostAccessBlock) carries, besides the DISPLAYED max for each pool, a
		// slightly-lower "base/secondary" value the client reads into a parallel slot. From the four archetypes' retail
		// LOGIN snapshots (decode_statblock.py over Necro/Guardian/Assassin/Bear-Shaman): mana base = displayed-6,
		// stamina base = displayed-6, HP base = displayed - a per-class L1 delta (Necro 9, Guardian 10, Assassin/
		// BearShaman 8). Authoring the WHOLE per-class block (both the displayed AND base slots) keeps it internally
		// consistent - patching only the displayed slot and leaving the base at the Strie template's Rogue value was the
		// suspected #4 (wrong mana) / "gear vanished" inconsistency (docs/archive/handoff-2026-06-17-perclass-snapshot.md).
		// Returned in display units (the block scales ×100 at write time). #186/#4.
		public (uint Hp, uint HpBase, uint Mana, uint ManaBase, uint Stamina, uint StaminaBase) SnapshotVitals(Class @class)
		{
			var a = Archetype(@class);
			var hpDelta = (uint)(a.HpL1 - a.HpBaseL1);
			var hpBase = Health > hpDelta
				? Health - hpDelta
				: Health;
			var manaBase = Mana > 6
				? Mana - 6
				: 0; // 0-pool classes stay 0; mana base is 6 below the displayed max
			var staminaBase = Stamina > 6
				? Stamina - 6
				: 0;
			return (Health, hpBase, Mana, manaBase, Stamina, staminaBase);
		}

		// Class/level vitals. The LEVEL-1 values are anchored to the retail character sheet (read off the live
		// client 2026-06-17, cross-checked against the world-entry FullChar snapshot stat block - see
		// docs/archive/handoff-2026-06-17-perclass-snapshot.md and #242): all 12 classes are now retail-anchored at L1 -
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
				{
					return (130, 60, 0, 0, 179, 54, 120);
				}

				case Class.Conqueror:
				{
					return (168, 60, 0, 0, 178, 54, 160);
				}

				// Dark Templar - Soldier hybrid, BOTH. Retail L1 (login snapshot, #242): HP 128 (base 120),
				// Mana 281, Stamina 178.
				case Class.DarkTemplar:
				{
					return (128, 60, 281, 30, 178, 54, 120);
				}

				// Rogues - STAMINA ONLY. Each class differs at L1 (login snapshots, #242): Assassin HP 98 (base 90) /
				// Stam 236, Barbarian HP 118 (base 110) / Stam 178, Ranger HP 133 (base 120) / Stam 213. Growth is
				// fitted to the Assassin's TWO captured points (L1 Strie 98/236, L23 AssassinL23 1270/736 -> ~53 HP /
				// ~23 Stam per level) and reused for all three until per-class multi-level captures land.
				case Class.Assassin:
				{
					return (98, 53, 0, 0, 236, 23, 90);
				}

				case Class.Barbarian:
				{
					return (118, 53, 0, 0, 178, 23, 110);
				}

				case Class.Ranger:
				{
					return (133, 53, 0, 0, 213, 23, 120);
				}

				// Ranged Priests - MANA ONLY. Retail L1 (login snapshots, #242): Priest of Mitra and Tempest of Set
				// both HP 88 (base 80) / Mana 171 / no stamina pool.
				case Class.PriestOfMitra:
				case Class.TempestOfSet:
				{
					return (88, 45, 171, 30, 0, 0, 80);
				}

				// Bear Shaman - Priest melee hybrid, BOTH. Retail L1: HP 108 (base 100), Mana 171, Stamina 178.
				case Class.BearShaman:
				{
					return (108, 45, 171, 30, 178, 39, 100);
				}

				// Herald of Xotli - Mage melee hybrid, BOTH. Retail L1 (login snapshot, #242): HP 89 (base 80),
				// Mana 246, Stamina 121.
				case Class.HeraldOfXotli:
				{
					return (89, 38, 246, 40, 121, 32, 80);
				}

				// Pure Mages - MANA ONLY. Retail L1 (login snapshots, #242): Necromancer and Demonologist both HP 89
				// (base 80) / Mana 246 / no stamina pool.
				case Class.Demonologist:
				case Class.Necromancer:
				{
					return (89, 38, 246, 40, 0, 0, 80);
				}

				default:
				{
					return (98, 47, 150, 30, 150, 41, 90);
				}
			}
		}
	}
}
