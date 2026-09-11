using System;
using System.Collections.Generic;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.FullCharSnapshot;

namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     Live current+max vitals for one world entity (player or NPC). The world is authoritative in memory, so
	///     this is session/world state and is never persisted - only the player's <c>CharacterMaxStats</c> is.
	///     Health/Mana/Stamina are player-facing values (the ×100 wire scale is applied by
	///     <see cref="CharacterStatBlock" /> when sent in a <c>0x96B8DC59</c> update).
	/// </summary>
	public sealed class EntityVitals
	{
		public bool IsDead => Health == 0;
		public uint Health { get; set; }
		public uint Mana { get; set; }
		public uint MaxHealth { get; set; }
		public uint MaxMana { get; set; }
		public uint MaxStamina { get; set; }
		public uint Stamina { get; set; }

		/// <summary>
		///     The part of each max pool that does not come from gear or buffs - the character's persisted
		///     <c>CharacterMaxStats</c> plus its class/level base. Kept so a bonus can be re-applied as a total rather
		///     than added and subtracted: a buff that raises max health has to give it back when it ends, and an
		///     add/subtract pair drifts the moment the buff is refreshed, re-applied on relog or cleared by death.
		/// </summary>
		public uint BaseMaxHealth { get; private set; }

		/// <summary>
		///     What the three max stat ids (0x0001/0x01F9/0x01FB) carry on the wire: the class base, which the client
		///     tops up with its own attribute, gear and feat terms to produce the number it draws. Set by
		///     <see cref="FromCharacter" />; null for an NPC, which has no class curve and whose reported max is its
		///     max.
		///     <para>
		///         MaxHealth/MaxMana/MaxStamina are this server's ceiling - what regen fills toward, and equal to the
		///         number the client draws - so they are a different quantity from this. Sending the ceiling here makes
		///         the client top up an already-topped-up value and draw 1754 where retail draws 1270. Retail states
		///         the split in one packet: AssassinL23's world-entry push carries <c>HpBase 77200</c> beside
		///         <c>HpCur 127000</c>, and <c>ManaBase 0</c> beside <c>ManaCur 7400</c> - a 0 base even though the
		///         character wears a +74 mana item, which is what keeps the mana bar hidden for a class that has no
		///         mana pool.
		///     </para>
		/// </summary>
		public (uint Health, uint Mana, uint Stamina)? ClassBasePools { get; private set; }
		// Held in the raw wire hundredths the class curve is stored in, not display units: Guardian stamina is 172.5
		// at level 1, and truncating it to 172 would put a value on the wire that the class curve never produces.

		public uint BaseMaxMana { get; private set; }
		public uint BaseMaxStamina { get; private set; }

		/// <summary>
		///     Sets one max pool to <c>base + bonus</c>, where the bonus is the gear plus active-buff total for that
		///     stat in display units. Returns the new max so the caller can push exactly what the client should show -
		///     a buff sync reports the stat's buffed value, but the wire's <c>HealthMax</c> is the whole pool, not the
		///     bonus.
		///     <para>
		///         A shrinking max clamps current down with it, so a buff ending cannot leave a player above their bar; a
		///         growing one leaves current where it is, so it fills by regen rather than jumping.
		///     </para>
		/// </summary>
		public uint SetMaxFromBonus(CharacterStatId stat, int bonus)
		{
			switch (stat)
			{
				case CharacterStatId.HealthMax:
				{
					MaxHealth = Add(BaseMaxHealth, bonus);
					Health = ClampToMax(Health, MaxHealth);
					return MaxHealth;
				}

				case CharacterStatId.ManaMax:
				{
					MaxMana = Add(BaseMaxMana, bonus);
					Mana = ClampToMax(Mana, MaxMana);
					return MaxMana;
				}

				case CharacterStatId.StaminaMax:
				{
					MaxStamina = Add(BaseMaxStamina, bonus);
					Stamina = ClampToMax(Stamina, MaxStamina);
					return MaxStamina;
				}

				default:
				{
					throw new ArgumentOutOfRangeException(nameof(stat), stat, "not one of the three max-pool stats");
				}
			}
		}

		/// <summary>
		///     Recomputes the three <c>BaseMax*</c> pools from a new set of attribute scores, for when a buff, a debuff
		///     or an equip moves an attribute mid-session. Returns true when anything actually moved.
		///     <para>
		///         Each pool is <c>class base + sum(weight x attribute) + class row + flat pool bonuses</c>. The flat
		///         half is re-applied by <see cref="SetMaxFromBonus" /> on every buff change; the attribute half is
		///         computed in <see cref="FromCharacter" /> and only moves when this runs, which is what makes a +10
		///         Constitution buff worth its +80 max health on an Assassin.
		///     </para>
		///     <para>
		///         This sets the base only. The caller must re-run <see cref="SetMaxFromBonus" /> for each pool
		///         afterwards, or the flat gear and buff term is dropped along with the stale base it was sitting on.
		///     </para>
		/// </summary>
		public bool RebaseFromAttributes(ConanClass @class, ushort level, VitalAttributes attributes,
			IReadOnlyDictionary<CharacterStatId, int>? classBase = null)
		{
			var max = CharacterMaxStats.For(@class, level, attributes, classBase);
			if (BaseMaxHealth == max.Health && BaseMaxMana == max.Mana && BaseMaxStamina == max.Stamina)
				return false;

			BaseMaxHealth = max.Health;
			BaseMaxMana = max.Mana;
			BaseMaxStamina = max.Stamina;
			return true;
		}

		/// <summary>Applies a health delta (negative = damage, positive = heal), clamped to <c>[0, MaxHealth]</c>.</summary>
		public void ApplyHealthDelta(int delta)
		{
			var next = Health + delta;
			if (next < 0)
				next = 0;
			else if (next > MaxHealth)
				next = MaxHealth;

			Health = (uint)next;
		}

		/// <summary>Applies a mana delta (negative = spend, positive = restore), clamped to <c>[0, MaxMana]</c>.</summary>
		public void ApplyManaDelta(int delta)
		{
			var next = Mana + delta;
			if (next < 0)
				next = 0;
			else if (next > MaxMana)
				next = MaxMana;

			Mana = (uint)next;
		}

		/// <summary>
		///     Regenerates the three pools by the given amounts, each clamped to its max. Returns true if any pool
		///     actually increased, so the caller can skip pushing a vitals update when nothing changed (at full, for
		///     instance). Driven by the game loop.
		/// </summary>
		public bool Regenerate(uint health, uint stamina, uint mana)
		{
			var changed = false;
			if (health > 0 && Health < MaxHealth)
			{
				Health = ClampToMax(Health + health, MaxHealth);
				changed = true;
			}

			if (stamina > 0 && Stamina < MaxStamina)
			{
				Stamina = ClampToMax(Stamina + stamina, MaxStamina);
				changed = true;
			}

			if (mana > 0 && Mana < MaxMana)
			{
				Mana = ClampToMax(Mana + mana, MaxMana);
				changed = true;
			}

			return changed;
		}

		/// <summary>
		///     Restores all three pools to <paramref name="percentOfMax" />% of their max - a resurrection point's
		///     <c>RestorationLevel</c>. Designed data: of the 538 imported points 344 restore 10%, 157 restore 100%,
		///     and the rest 7/20/25/30/50/70, so the common point leaves you vulnerable and a starter or safe point
		///     brings you back whole.
		///     <para>
		///         Always leaves at least 1 in each pool, so a 0 or garbage value can never revive a player dead. A
		///         non-positive percent means "no data" and restores full.
		///     </para>
		///     <para>
		///         The exact semantic is inferred: the RDB names the field but not what it scales. Health is
		///         near-certain; whether it also scales stamina and mana is unverified, and applying it to all three is
		///         the reading that matches the name. An in-client A/B - dying near a 10% point against a 100% one -
		///         settles it.
		///     </para>
		/// </summary>
		public void ReviveTo(float percentOfMax)
		{
			if (percentOfMax <= 0f)
			{
				ReviveToFull();
				return;
			}

			var fraction = Math.Min(percentOfMax, 100f) / 100f;
			Health = Math.Max(1, (uint)(MaxHealth * fraction));
			Stamina = Math.Max(1, (uint)(MaxStamina * fraction));
			Mana = Math.Max(1, (uint)(MaxMana * fraction));
		}

		/// <summary>
		///     Re-seeds this entity's max pools from a freshly computed set and fills every pool to it - the
		///     <c>.level</c> and max-changed path, which recomputes through <see cref="FromCharacter" /> and then has
		///     to land the result on the live object the world already holds.
		///     <para>
		///         It carries the buff engine's baseline (<see cref="BaseMaxHealth" /> and friends) and the wire's
		///         <see cref="ClassBasePools" /> across too, not just the three maxima. Both are functions of the
		///         character's class and level, so copying the maxima alone leaves a levelled character announcing the
		///         class base of the level it came from.
		///     </para>
		/// </summary>
		public void ReseedMaxFrom(EntityVitals seeded)
		{
			ArgumentNullException.ThrowIfNull(seeded);
			MaxHealth = seeded.MaxHealth;
			MaxMana = seeded.MaxMana;
			MaxStamina = seeded.MaxStamina;
			BaseMaxHealth = seeded.BaseMaxHealth;
			BaseMaxMana = seeded.BaseMaxMana;
			BaseMaxStamina = seeded.BaseMaxStamina;
			ClassBasePools = seeded.ClassBasePools;
			ReviveToFull();
		}

		/// <summary>Restores all three pools to their max (a full revive - an NPC leashing home, or a fallback).</summary>
		public void ReviveToFull()
		{
			Health = MaxHealth;
			Stamina = MaxStamina;
			Mana = MaxMana;
		}

		/// <summary>
		///     The vitals as a <c>0x96B8DC59</c> stat block, emitting the current and max id for each of Health,
		///     Stamina and Mana. The two id sets are in-client verified: current = {0x001B,0x01F8,0x01FA}, max =
		///     {0x0001,0x01F9,0x01FB} (see <see cref="CharacterStatId" />). A regen or damage delta mutates the current
		///     value, so it must ride the current id; under the max id it would grow the player's max bar instead of
		///     refilling current. The max ids carry <see cref="ClassBasePools" />, not this server's ceiling.
		/// </summary>
		public CharacterStatBlock ToVitalsBlock()
		{
			var (baseHealth, baseMana, baseStamina) = ClassBasePools
				?? (MaxHealth * CharacterStatId.HealthMax.GetScale(), MaxMana * CharacterStatId.ManaMax.GetScale(),
					MaxStamina * CharacterStatId.StaminaMax.GetScale());

			return new CharacterStatBlock()
				.Set(CharacterStatId.HealthCurrent, Health)
				.SetRaw(CharacterStatId.HealthMax, baseHealth)
				.Set(CharacterStatId.StaminaCurrent, Stamina)
				.SetRaw(CharacterStatId.StaminaMax, baseStamina)
				.Set(CharacterStatId.ManaCurrent, Mana)
				.SetRaw(CharacterStatId.ManaMax, baseMana);
		}

		/// <summary>
		///     What a <c>RegenIIR</c> has to carry to bring these three pools up to their max: each pool's shortfall in
		///     the wire's x100 vitals scale, and 0 for a pool that is already full.
		///     <para>
		///         A RegenIIR is a delta the client adds to its own current vitals, so the only correct value is this
		///         entity's own shortfall. A replayed constant would be added on top of a pool world entry has already
		///         announced as full, leaving the character above its own maximum.
		///     </para>
		/// </summary>
		public (int Health, int Mana, int Stamina) ToRefillDeltas()
		{
			return (Shortfall(MaxHealth, Health), Shortfall(MaxMana, Mana), Shortfall(MaxStamina, Stamina));

			static int Shortfall(uint max, uint current)
			{
				return current >= max
					? 0
					: (int)((max - current) * 100);
			}
		}

		/// <summary>
		///     The three currents only - what retail sends when a pool moves, as opposed to when it is seeded.
		///     <para>
		///         Measured over the whole corpus (<c>--report vitals-shape</c>): of the 4,900 <c>0x96B8DC59</c> blocks
		///         retail sends on a handle that is not the session's own character, 3,694 carry exactly
		///         <c>{HealthCurrent, StaminaCurrent, ManaCurrent}</c> and only 16 carry the six-record current+max
		///         form - and those 16 sit in the multi-character captures, another player coming into interest range,
		///         which is a seed rather than a change. The six-record form on the session's own handle is likewise
		///         rare (81 blocks) against a flood of single-stat pushes.
		///     </para>
		///     <para>
		///         The max ids therefore belong to the seed, not to the update. Sending them on every hit would be
		///         three extra records per entity per swing on the hottest packet path in the game, and for an NPC they
		///         would be a fabrication besides: an NPC has no class curve, so <see cref="ClassBasePools" /> is null
		///         and the max ids fall back to this server's own ceiling, which is not the quantity that slot holds
		///         for a player.
		///     </para>
		/// </summary>
		public CharacterStatBlock ToCurrentsBlock()
		{
			return new CharacterStatBlock()
				.Set(CharacterStatId.HealthCurrent, Health)
				.Set(CharacterStatId.StaminaCurrent, Stamina)
				.Set(CharacterStatId.ManaCurrent, Mana);
		}

		// What the last own-handle push stated, so the next one can carry only what moved. uint.MaxValue is "never
		// pushed", which no pool can equal, so the first push after world entry always states all three.
		private uint _pushedHealth = uint.MaxValue, _pushedStamina = uint.MaxValue, _pushedMana = uint.MaxValue;

		/// <summary>
		///     The currents that have changed since the last call, and nothing else - an empty block when none have.
		///     Stamps what it returns, so it is a state transition and must be called once per push.
		/// </summary>
		/// <remarks>
		///     <para>
		///         A single-stat block is the own-handle idiom: the corpus splits the own-handle blocks as
		///         <c>[0x01FA]</c> x1171, <c>[0x001B]</c> x211 and <c>[0x01F8]</c> x121 against only x12 for the
		///         three-current form, while that three-current form is what retail says about other entities (x4,676).
		///     </para>
		///     <para>
		///         Pushing all three regardless costs a health record on every tick of a fight: HP regen halts in
		///         combat but stamina's does not, so the regen tick fires every second and would restate
		///         <c>HealthCurrent</c> with it, announcing a change that did not happen.
		///     </para>
		/// </remarks>
		public CharacterStatBlock ToChangedCurrentsBlock()
		{
			var block = new CharacterStatBlock();
			if (Health != _pushedHealth)
				block.Set(CharacterStatId.HealthCurrent, Health);
			if (Stamina != _pushedStamina)
				block.Set(CharacterStatId.StaminaCurrent, Stamina);
			if (Mana != _pushedMana)
				block.Set(CharacterStatId.ManaCurrent, Mana);

			(_pushedHealth, _pushedStamina, _pushedMana) = (Health, Stamina, Mana);
			return block;
		}

		/// <summary>
		///     Forgets what was last pushed, so the next <see cref="ToChangedCurrentsBlock" /> restates every current.
		///     Call it wherever the client's copy is reseeded from something other than a push - world entry, a
		///     resurrection - because after that the record of what the client holds no longer describes its state.
		/// </summary>
		public void ForgetPushedCurrents()
		{
			_pushedHealth = _pushedStamina = _pushedMana = uint.MaxValue;
		}

		/// <summary>
		///     Full-health vitals seeded from a character's persisted max stats (class and level defaults if it has
		///     none), plus the equipped-gear vital bonuses, so worn +HP/+Stamina/+Mana raises the max and the regen bar
		///     can fill past the class base. The world model's single seed point for a player's live vitals, used both
		///     by combat and by the world simulation when a player is registered.
		/// </summary>
		/// <param name="statBonuses">
		///     The <see cref="MaxStatAggregator" /> output - display-unit totals by stat. Null or absent means no gear
		///     bonus.
		/// </param>
		public static EntityVitals FromCharacter(Character character, IReadOnlyDictionary<CharacterStatId, int>? statBonuses = null,
			IReadOnlyDictionary<CharacterStatId, int>? classBase = null)
		{
			// The ceiling is retail's displayed maximum, built from the same three terms the client builds it from:
			// the class/level curve, the attribute-scaled top-up, and the class/level ClassAttributeBases pool rows
			// (the "class-feat" HealthMax, +386 for a level-80 Necromancer). CharacterMaxStats.For owns that
			// arithmetic, and the attributes it scales off are this character's totals, which is why gear has to be
			// folded into them and not only into the flat pool bonus below.
			//
			// The client's own version of this sum is readable off the ClientInterface channel and agrees term for
			// term - for the level-80 Necromancer: 2371 class base + 3000 attribute (8 x 375 Constitution) + 386
			// class-feat + 584 item = the captured 6341.
			//
			// The remaining term - buffs that raise a max pool (Focused Mind's +mana, Sustaining Wrath's +health) - is
			// not here because it is not fixed at world entry: BuffStatEffects re-applies it through SetMaxFromBonus on
			// every change to the entity's live buffs, on top of the base these terms establish. A buff that raises an
			// attribute re-runs For through RebaseFromAttributes, on the same path and for the same reason.
			var max = character.MaxVitals(VitalAttributes.From(classBase, statBonuses), classBase);

			var vitals = FromMax(max.Health, max.Mana, max.Stamina);
			vitals.ClassBasePools = ClassBaseWire(character);
			vitals.BaseMaxHealth = vitals.MaxHealth;
			vitals.BaseMaxMana = vitals.MaxMana;
			vitals.BaseMaxStamina = vitals.MaxStamina;
			foreach (var stat in MaxPools)
				vitals.SetMaxFromBonus(stat, statBonuses?.GetValueOrDefault(stat) ?? 0);

			vitals.ReviveToFull();
			// A character that logged out dead is still dead: seed current health at 0, max staying full, so the world
			// model agrees with the dead login snapshot and the respawn paths, which gate on IsDead, work.
			if (character.IsDead)
				vitals.Health = 0;
			return vitals;
		}

		/// <summary>
		///     This entity's live ceiling as a <see cref="CharacterMaxStats" />, for the wire builders that take one
		///     (the FullChar snapshot's stat block, the spawn's CharacterIIR). The live value is the one that knows
		///     the character's gear, so those builders take it rather than re-deriving a class/level curve.
		/// </summary>
		public CharacterMaxStats ToMaxStats()
		{
			return new CharacterMaxStats
			{
				Health = MaxHealth,
				Mana = MaxMana,
				Stamina = MaxStamina
			};
		}

		/// <summary>Creates a full-health entity (current == max) from max values.</summary>
		public static EntityVitals FromMax(uint health, uint mana, uint stamina)
		{
			return new EntityVitals
			{
				Health = health,
				MaxHealth = health,
				Mana = mana,
				MaxMana = mana,
				Stamina = stamina,
				MaxStamina = stamina
			};
		}

		/// <summary>The three max-pool stats, in the order <see cref="SetMaxFromBonus" /> accepts.</summary>
		public static readonly CharacterStatId[] MaxPools =
			[CharacterStatId.HealthMax, CharacterStatId.ManaMax, CharacterStatId.StaminaMax];

		// base + bonus, floored at 0: a debuff can empty a pool but never take it negative.
		private static uint Add(uint baseMax, int bonus)
		{
			var total = baseMax + bonus;
			return total < 0
				? 0
				: (uint)total;
		}

		// The class base for this character's three pools, in the wire's hundredths - what the max stat ids carry.
		// Straight off the class curve with no attribute, gear or feat term: retail's AssassinL23 push carries
		// HpBase 77200 against a drawn 127000, and ManaBase 0 against a drawn 7400.
		private static (uint Health, uint Mana, uint Stamina) ClassBaseWire(Character character)
		{
			return CharacterMaxStats.ClassBaseHundredths(character.Class, character.Level);
		}

		private static uint ClampToMax(uint value, uint max)
		{
			return value > max
				? max
				: value;
		}
	}
}
