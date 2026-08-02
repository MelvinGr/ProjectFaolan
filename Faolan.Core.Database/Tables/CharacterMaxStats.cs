using System;
using System.Collections.Generic;
using Faolan.Core.Enums;

namespace Faolan.Core.Database.Tables
{
	// A computed VALUE, not a table (#578). It used to be a one-to-one row beside Character, but it is a pure
	// function of (Class, Level) and the character's attributes - so storing it only created something that could
	// disagree with the level, which is exactly what happened: a combat level-up wrote the new Level and left the row
	// behind, and the next login re-seeded current health from the stale ceiling. Build it with For().
	public class CharacterMaxStats
	{
		// Player-facing values (what the character sheet shows), e.g. Health 1270. The on-wire ×100 vitals scale
		// is applied at send time, not stored.
		public uint Health { get; set; }
		public uint Mana { get; set; }
		public uint Stamina { get; set; }

		// The FullChar snapshot stat block (TailsPostAccessBlock) carries, besides the character's CURRENT value for
		// each pool, the class BASE the client reads into a parallel slot. Authoring the WHOLE per-class block (both
		// slots) keeps it internally consistent - patching only one slot and leaving the other at the Strie template's
		// Rogue value was the suspected #4 (wrong mana) / "gear vanished" inconsistency.
		// Returned in display units (the block scales ×100 at write time). #186/#4/#580.
		public (uint Hp, uint HpBase, uint Mana, uint ManaBase, uint Stamina, uint StaminaBase) SnapshotVitals(ConanClass @class, ushort level)
		{
			// The three "base" slots are the CLASS BASE - the same quantity a live vitals push carries in stat
			// 0x0001/0x01F9/0x01FB - which the client tops up with its own attribute/gear/feat terms to get the number
			// it draws. The three current slots are this instance, which IS that topped-up number (see For()).
			var (hpBase, manaBase, staminaBase) = ClassBaseHundredths(@class, level);

			return (Health, hpBase / 100, Mana, manaBase / 100, Stamina, staminaBase / 100);
		}

		/// <summary>
		///     A character's three pools, in DISPLAY units, BEFORE the flat gear/feat pool bonuses (a +75 health ring)
		///     the caller adds on top - see <c>EntityVitals.FromCharacter</c>. Two terms, both from the client's own
		///     <c>HitsManaAndStamina_c</c> table (RDB 1000511):
		///     <list type="bullet">
		///         <item>the class/level BASE curve (<see cref="ClassBaseHundredths" />), columns [2..7], and</item>
		///         <item>the ATTRIBUTE-scaled top-up (<see cref="AttributeScaledHundredths" />), columns [8..13],</item>
		///     </list>
		///     plus the class/level <c>ClassAttributeBases</c> pool rows (#444) when the caller has them - the
		///     "class-feat" HealthMax that gives a Ranger +5 health at level 1 and a level-80 Necromancer +386.
		///     <para>
		///         This is retail's DISPLAYED maximum, and it is what the server's ceiling must be: retail's own
		///         vitals push carries the full pool under the CURRENT id (AssassinL23 at full: <c>HpCur 127000</c>) while
		///         the base rides the max id (<c>HpBase 77200</c>), so a server ceiling that is not the displayed max
		///         shows up in-client as a current above (or below) the bar - health 1314 against a 1270 max (#580).
		///     </para>
		///     <para>
		///     <para>
		///         ⚠️ DemoL80's 177 is now NARROWED, and it is not one thing (#588). `ActiveWeaponSet` is never set by
		///         the character importer, so every imported character defaults to <c>Primary</c> - and DemoL80's
		///         SECONDARY set (Jagged Cut +12 Con and Fetish of Caged Fiends +35, against Mindraker's +34) is worth
		///         13 more Constitution, i.e. 104 more health. That takes the shortfall 177 -> 73.
		///         <list type="bullet">
		///             <item>The remaining 73 is NOT Constitution: 73 is not a multiple of the health weight (8), so
		///                 no attribute total produces it. It is a FLAT health term - a <c>HealthMax</c> record worth
		///                 73 that our item data does not carry.</item>
		///             <item>It is not the vanity slots either, and that is worth writing down because they look like
		///                 the obvious candidate: DemoL80's Canvas Belt (+4 Con) and Northfur Cloak (+23 Con) would add
		///                 216 health, overshooting by 143. Excluding them, which is what we do, is right.</item>
		///             <item>Feats are ruled out separately (#589): the pipeline is intact and DemoL80 trained none
		///                 that grant <c>HealthMax</c>.</item>
		///         </list>
		///         What is NOT recoverable today is which weapon set was drawn. There is no capture signal for it -
		///         the swap is observable (the server stamps the newly-active main slot on a
		///         <c>SwitchWeaponsButton</c>) but a login carries no such event, and the client derives its own
		///         display from the equipment container. So the 177 -> 73 above is arithmetic that FITS, not a
		///         measurement, and the default stays <c>Primary</c> rather than being guessed the other way.
		///     </para>
		///         VERIFIED, not fitted. It reproduces the retail character sheet exactly at:
		///         all TWELVE classes at level 1 (Guardian 130, Conqueror 168, Dark Templar 128/281/178, Assassin
		///         98/236, Barbarian 118, Ranger 133, the priests 88/171, Bear Shaman 108/171/178, Herald 89/246/121,
		///         the mages 89/246 - every health, mana and stamina anchor, including Guardian's 178.5 stamina
		///         rounding to the captured 179); and at level 23/24/80 against the four captured characters'
		///         world-entry pushes - AssassinL23 (Assassin L23) 1270/1148/74, Hakotep (Herald L24) 1414/759/1584,
		///         NecroL80 (Necromancer L80) 6341 health and 6523 mana, DemoL80 (Demonologist L80) 5830 mana. Ten of the
		///         eleven measurable pools land on the captured value to the point; the eleventh (DemoL80's health) is
		///         177 low because our item data for that character is short, not because the curve is.
		///     </para>
		/// </summary>
		public static CharacterMaxStats For(ConanClass @class, ushort level, VitalAttributes attributes,
			IReadOnlyDictionary<CharacterStatId, int>? classAttributeBase = null)
		{
			var (baseHealth, baseMana, baseStamina) = ClassBaseHundredths(@class, level);
			var (attrHealth, attrMana, attrStamina) = AttributeScaledHundredths(@class, attributes);

			return new CharacterMaxStats
			{
				Health = Pool(baseHealth, attrHealth, CharacterStatId.HealthMax),
				Mana = Pool(baseMana, attrMana, CharacterStatId.ManaMax),
				Stamina = Pool(baseStamina, attrStamina, CharacterStatId.StaminaMax)
			};

			// A class WITHOUT a pool (an Assassin's mana) has a 0 base AND 0 attribute columns, and must stay 0: the
			// client hides a bar whose max is 0, and retail keeps sending 0 there even for an Assassin wearing a +74
			// mana item (whose 74 is a flat gear bonus the caller adds afterwards).
			uint Pool(uint classBase, uint attributeTerm, CharacterStatId pool)
			{
				if (classBase == 0)
					return 0;

				// ClassAttributeBase.Amount is the RAW WIRE value and the pools are the ×100 stats, so it divides by
				// the stat's scale exactly like the attributes do. Adding it raw put a level-1 Necromancer at 89 + 100
				// health.
				var hundredths = classBase + attributeTerm + (uint)Math.Max(0, classAttributeBase?.GetValueOrDefault(pool) ?? 0);

				// One rounding, at the end. The wire is hundredths and the sheet is whole points, and Guardian level 1
				// is the case that proves it rounds rather than truncates: base 172.5 + 6 attribute points = 178.5,
				// and the captured sheet reads 179.
				return (hundredths + 50) / 100;
			}
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
		// ⚠️ THIS IS NOT THE DISPLAYED MAX - it is the first of its terms. The client tops it up with the attribute
		// term below plus gear and feats, and an L23 Assassin whose sheet reads 1270 has a class base of 772. Wiring
		// it as the ceiling would LOWER every character's pools; the ceiling is For().
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

		/// <summary>
		///     The ATTRIBUTE-scaled part of each pool, in WIRE hundredths: what the client adds to the class base off
		///     the character's Strength/Constitution/… . RDB 1000511's trailing six columns are the per-point weights
		///     in thousandths - <c>8000,0,0,2000,2000,2000</c> for an Assassin, i.e. 8 health per Constitution and 2
		///     stamina per point of each of three attributes.
		/// </summary>
		/// <remarks>
		///     ✅ SETTLED FROM THE CLIENT (#587), which the corpus could not do. Each pool has its own accessor and
		///     the stat ids it reads ARE the attribute set - no inference:
		///     <list type="bullet">
		///         <item><c>FUN_0111aa30</c> health - reads <c>0x326</c> alone, and calls the one-weight lookup
		///             <c>FUN_0113e730</c>, which returns <c>w[8]/10 x score</c>.</item>
		///         <item><c>FUN_0111ab60</c> mana - reads <c>0x328</c> and <c>0x32A</c>, and ONLY those two, then
		///             calls the two-weight <c>FUN_0113e7d0</c>: <c>w[9]/10 x Intelligence + w[10]/10 x Wisdom</c>.</item>
		///         <item><c>FUN_0111ad20</c> stamina - reads <c>0x324</c>, <c>0x326</c> and <c>0x32E</c>.</item>
		///     </list>
		///     So mana really is Intelligence + Wisdom and <c>0x32E</c> Dexterity appears in the STAMINA accessor and
		///     nowhere near mana. It used to be the one term with slack - every captured caster happens to carry equal
		///     Wisdom and Dexterity (110/110 on both level-80 mages, 33/33 on the Herald), so the corpus could not
		///     separate them and this shipped as a reading. It is now a measurement.
		///     <para>
		///         The column ORDER is settled with it, which the table alone also cannot show (every class's weights
		///         are equal within a pool - health 8; mana 3,3; stamina 2,2,2): <c>[8]</c> Constitution, <c>[9]</c>
		///         Intelligence, <c>[10]</c> Wisdom. See docs/rdb/client-data-tables.md.
		///     </para>
		/// </remarks>
		public static (uint Health, uint Mana, uint Stamina) AttributeScaledHundredths(ConanClass @class, VitalAttributes attributes)
		{
			var (perConstitution, perIntelligence, perWisdom, perStrength, perDexterity, perStaminaConstitution) = AttributeScalingCurve(@class);

			// The weights are thousandths and the pools are hundredths, so each product divides by 10.
			return (Term((perConstitution, attributes.Constitution)),
				Term((perIntelligence, attributes.Intelligence), (perWisdom, attributes.Wisdom)),
				Term((perStrength, attributes.Strength), (perDexterity, attributes.Dexterity), (perStaminaConstitution, attributes.Constitution)));

			uint Term(params (int PerPoint, uint Score)[] terms)
			{
				var total = 0L;
				foreach (var (perPoint, score) in terms)
					total += (long)perPoint * score / 10;

				return (uint)total;
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

		// RDB 1000511's TRAILING six words per class, in thousandths per attribute point: the health column, the two
		// mana columns, and the three stamina columns. A class without a pool carries zeroes in that pool's columns
		// (an Assassin's `8000,0,0,2000,2000,2000`), which is what keeps a poolless class at 0 rather than growing a
		// bar off its attributes. Copied from `--report rdb-class-vitals` like the base curve above.
		private static (int Health, int Mana1, int Mana2, int Stamina1, int Stamina2, int Stamina3) AttributeScalingCurve(ConanClass @class)
		{
			return @class switch
			{
				ConanClass.Guardian or ConanClass.Conqueror or ConanClass.Assassin or ConanClass.Barbarian or ConanClass.Ranger => (8000, 0, 0, 2000, 2000, 2000),
				ConanClass.DarkTemplar or ConanClass.BearShaman or ConanClass.HeraldOfXotli => (8000, 3000, 3000, 2000, 2000, 2000),
				ConanClass.PriestOfMitra or ConanClass.TempestOfSet or ConanClass.Necromancer or ConanClass.Demonologist => (8000, 3000, 3000, 0, 0, 0),

				// Same as ClassBaseCurve: no default arm, for the same reason - these words come out of the client's
				// own table and an unknown class is a corrupt Character.Class.
				_ => throw new ArgumentOutOfRangeException(nameof(@class), @class, "no class attribute-scaling row - RDB 1000511 covers exactly the twelve playable classes.")
			};
		}
	}
}
