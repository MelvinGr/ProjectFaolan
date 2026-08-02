using System;
using Faolan.Core.Enums;

namespace Faolan.Core.Database.Tables
{
	// A computed VALUE, not a table (#578). It used to be a one-to-one row beside Character, but it is a pure
	// function of (Class, Level) - so storing it only created something that could disagree with the level, which is
	// exactly what happened: a combat level-up wrote the new Level and left the row behind, and the next login
	// re-seeded current health from the stale ceiling. Build it with Character.MaxVitals().
	public class CharacterMaxStats
	{
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
		// suspected #4 (wrong mana) / "gear vanished" inconsistency.
		// Returned in display units (the block scales ×100 at write time). #186/#4.
		public (uint Hp, uint HpBase, uint Mana, uint ManaBase, uint Stamina, uint StaminaBase) SnapshotVitals(ConanClass @class)
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

		// Retail's CLASS BASE for the three pools at `level`, in WIRE hundredths - the exact value retail puts in
		// stat 0x0001 / 0x01F9 / 0x01FB. Sourced from the client's own `HitsManaAndStamina_c` table (RDB 1000511,
		// `--report rdb-class-vitals`), whose 95 records are 5 races x 19 class slots carrying 14 x i32; the six
		// words below are `[2..4]` (base, hundredths) and `[5..7]` (growth, thousandths) of a class's record. The
		// growth divides by 10 because it is stored one decimal finer than the base.
		//
		//   base(level) = baseWord + growthWord * (level - 1) / 10
		//
		// VERIFIED, not fitted: it reproduces the max in all 16 captured retail level-up pushes (the 0x96B8DC59 that
		// follows a NewLevelIIR) exactly, across 6 classes and levels 2-5, including Guardian's non-integral stamina
		// (172.5 + 25.3 = 197.8 -> the captured 19780). See docs/rdb/client-data-tables.md.
		//
		// ⚠️ THIS IS NOT THE DISPLAYED MAX, and wiring it as one would LOWER every character's ceiling. Retail
		// routinely sends a base below the current in the same packet (#450); the client tops it up from gear, feats
		// and attributes, and an L23 Assassin whose sheet reads 1270 has a class base of 772. The remaining term is
		// tracked in #450/#3 - this is the first of its three sources, not the whole sum.
		public static (uint Health, uint Mana, uint Stamina) ClassBaseHundredths(ConanClass @class, ushort level)
		{
			ArgumentOutOfRangeException.ThrowIfZero(level); // level 0 is not a character state - creation allocates at 1
			var (hp, hpGrow, mana, manaGrow, stamina, staminaGrow) = ClassBaseCurve(@class);
			var steps = level - 1;
			return (At(hp, hpGrow), At(mana, manaGrow), At(stamina, staminaGrow));

			uint At(int b, int grow)
			{
				return b == 0
					? 0u
					: (uint)(b + grow * steps / 10);
			}
		}

		// RDB 1000511's six vitals words per class: (health, healthGrowth, mana, manaGrowth, stamina, staminaGrowth).
		// A 0 base means the class does not have that pool (the client hides the bar). These are the table's own
		// integers - the base in hundredths, the growth in thousandths - copied rather than computed so the numbers
		// can be diffed against a fresh `--report rdb-class-vitals` run.
		private static (int Health, int HealthGrowth, int Mana, int ManaGrowth, int Stamina, int StaminaGrowth) ClassBaseCurve(ConanClass @class)
		{
			return @class switch
			{
				ConanClass.Guardian => (12000, 40000, 0, 0, 17250, 25300),
				ConanClass.Conqueror => (16000, 36000, 0, 0, 17200, 25000),
				ConanClass.DarkTemplar => (12000, 37000, 27500, 15000, 17200, 25000),
				ConanClass.Assassin => (9000, 31000, 0, 0, 23000, 23000),
				ConanClass.Barbarian => (11000, 37000, 0, 0, 17200, 28000),
				ConanClass.Ranger => (12000, 26000, 0, 0, 20700, 28000),
				ConanClass.PriestOfMitra or ConanClass.TempestOfSet => (8000, 10000, 16500, 28000, 0, 0),
				ConanClass.BearShaman => (10000, 20000, 16500, 26000, 17200, 17000),
				ConanClass.HeraldOfXotli => (8000, 33000, 24000, 30000, 11500, 13000),
				ConanClass.Necromancer or ConanClass.Demonologist => (8000, 29000, 24000, 30000, 0, 0),

				// No default arm on purpose. The twelve above are the whole class list and each row is the client's
				// own, so an unknown class here is a corrupt Character.Class - not a case to answer with somebody
				// else's curve. A fabricated one would put a plausible max on the wire and be invisible.
				_ => throw new ArgumentOutOfRangeException(nameof(@class), @class, "no class base vitals curve - RDB 1000511 covers exactly the twelve playable classes.")
			};
		}

		// Class/level vitals. The LEVEL-1 values are anchored to the retail character sheet (read off the live
		// client 2026-06-17, cross-checked against the world-entry FullChar snapshot stat block - see
		// #242): all 12 classes are now retail-anchored at L1 -
		// Guardian 130/0/179, Conqueror 168/0/178, Dark Templar 128/281/178, Assassin 98/0/236, Barbarian
		// 118/0/178, Ranger 133/0/213, Bear Shaman 108/171/178, Priest of Mitra 88/171/0, Tempest of Set 88/171/0,
		// Necromancer 89/246/0, Demonologist 89/246/0, Herald of Xotli 89/246/121 (HP/Mana/Stamina). Which pools a
		// class HAS matches memory class_resource_mana_stamina (a 0 anchor zeroes the pool so the client hides that
		// bar). PER-LEVEL growth is still rough (no multi-level captures yet); only L1 is authoritative.
		public static CharacterMaxStats CreateDefaults(ConanClass @class, ushort level)
		{
			// No level-0 normalisation: DatabaseRepository.CreateCharacter allocates every row at level 1 and no
			// shipped character is below it, so a 0 here is a corrupt row and quietly serving it level-1 vitals would
			// make a broken character look like a new one.
			ArgumentOutOfRangeException.ThrowIfZero(level);
			var extra = level - 1;
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
		private static (int HpL1, int HpGrow, int ManaL1, int ManaGrow, int StaminaL1, int StaminaGrow, int HpBaseL1) Archetype(ConanClass @class)
		{
			switch (@class)
			{
				// Melee Soldiers - STAMINA ONLY. Retail L1 (login snapshots, #242): Guardian HP 130 (base 120) /
				// Stam 179, Conqueror HP 168 (base 160) / Stam 178.
				case ConanClass.Guardian:
				{
					return (130, 60, 0, 0, 179, 54, 120);
				}

				case ConanClass.Conqueror:
				{
					return (168, 60, 0, 0, 178, 54, 160);
				}

				// Dark Templar - Soldier hybrid, BOTH. Retail L1 (login snapshot, #242): HP 128 (base 120),
				// Mana 281, Stamina 178.
				case ConanClass.DarkTemplar:
				{
					return (128, 60, 281, 30, 178, 54, 120);
				}

				// Rogues - STAMINA ONLY. Each class differs at L1 (login snapshots, #242): Assassin HP 98 (base 90) /
				// Stam 236, Barbarian HP 118 (base 110) / Stam 178, Ranger HP 133 (base 120) / Stam 213. Growth is
				// fitted to the Assassin's TWO captured points (L1 Strie 98/236, L23 AssassinL23 1270/736 -> ~53 HP /
				// ~23 Stam per level) and reused for all three until per-class multi-level captures land.
				case ConanClass.Assassin:
				{
					return (98, 53, 0, 0, 236, 23, 90);
				}

				case ConanClass.Barbarian:
				{
					return (118, 53, 0, 0, 178, 23, 110);
				}

				case ConanClass.Ranger:
				{
					return (133, 53, 0, 0, 213, 23, 120);
				}

				// Ranged Priests - MANA ONLY. Retail L1 (login snapshots, #242): Priest of Mitra and Tempest of Set
				// both HP 88 (base 80) / Mana 171 / no stamina pool.
				case ConanClass.PriestOfMitra:
				case ConanClass.TempestOfSet:
				{
					return (88, 45, 171, 30, 0, 0, 80);
				}

				// Bear Shaman - Priest melee hybrid, BOTH. Retail L1: HP 108 (base 100), Mana 171, Stamina 178.
				case ConanClass.BearShaman:
				{
					return (108, 45, 171, 30, 178, 39, 100);
				}

				// Herald of Xotli - Mage melee hybrid, BOTH. Retail L1 (login snapshot, #242): HP 89 (base 80),
				// Mana 246, Stamina 121.
				case ConanClass.HeraldOfXotli:
				{
					return (89, 38, 246, 40, 121, 32, 80);
				}

				// Pure Mages - MANA ONLY. Retail L1 (login snapshots, #242): Necromancer and Demonologist both HP 89
				// (base 80) / Mana 246 / no stamina pool.
				case ConanClass.Demonologist:
				case ConanClass.Necromancer:
				{
					return (89, 38, 246, 40, 0, 0, 80);
				}

				// Same as ClassBaseCurve: no default arm. The twelve cases above are every playable class, so reaching
				// here means Character.Class is corrupt, and answering with an invented Rogue-ish row would hand the
				// client a plausible sheet built from nothing.
				default:
				{
					throw new ArgumentOutOfRangeException(nameof(@class), @class, "no class vitals anchors - the twelve playable classes are all listed above.");
				}
			}
		}
	}
}
