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
		///     The part of each max pool that does NOT come from gear or buffs - the character's persisted
		///     <c>CharacterMaxStats</c> plus its class/level base (#444). Kept so a bonus can be RE-applied as a total
		///     rather than added and subtracted: a buff that raises max health has to give it back when it ends, and an
		///     add/subtract pair drifts the moment the buff is refreshed, re-applied on relog or cleared by death.
		/// </summary>
		public uint BaseMaxHealth { get; private set; }

		public uint BaseMaxMana { get; private set; }
		public uint BaseMaxStamina { get; private set; }

		/// <summary>
		///     Sets one max pool to <c>base + bonus</c>, where the bonus is the gear + active-buff total for that stat in
		///     DISPLAY units. Returns the new max so the caller can push exactly what the client should show - a buff sync
		///     reports the stat's buffed VALUE, but the wire's <c>HealthMax</c> is the whole pool, not the bonus.
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
					MaxHealth = Add(BaseMaxHealth, bonus);
					Health = ClampToMax(Health, MaxHealth);
					return MaxHealth;
				case CharacterStatId.ManaMax:
					MaxMana = Add(BaseMaxMana, bonus);
					Mana = ClampToMax(Mana, MaxMana);
					return MaxMana;
				case CharacterStatId.StaminaMax:
					MaxStamina = Add(BaseMaxStamina, bonus);
					Stamina = ClampToMax(Stamina, MaxStamina);
					return MaxStamina;
				default:
					throw new ArgumentOutOfRangeException(nameof(stat), stat, "not one of the three max-pool stats");
			}
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
		///     actually increased, so the caller can skip pushing a vitals update when nothing changed (e.g. at full).
		///     Driven by the game loop (Gitea #404).
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
		///     <c>RestorationLevel</c> (#375). Real, designed data: of the 538 imported points 344 restore 10%, 157
		///     restore 100%, and the rest 7/20/25/30/50/70 - so the common point leaves you vulnerable and a
		///     starter/safe point brings you back whole. Reviving everyone at full flattened that away.
		///     Always leaves at least 1 in each pool, so a 0/garbage value can never revive a player dead. A
		///     non-positive percent means "no data" and restores full.
		///     The exact semantic is INFERRED: the RDB names the field but not what it scales. Health is near-certain;
		///     whether it also scales stamina/mana is unverified, and applying it to all three is the reading that
		///     matches the name. An in-client A/B (die near a 10% point vs a 100% one) settles it.
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

		/// <summary>Restores all three pools to their max (a full revive - an NPC leashing home, or a fallback).</summary>
		public void ReviveToFull()
		{
			Health = MaxHealth;
			Stamina = MaxStamina;
			Mana = MaxMana;
		}

		/// <summary>
		///     The vitals as a <c>0x96B8DC59</c> stat block, emitting the current AND max id for each of
		///     Health/Stamina/Mana. The current/max ids are in-client verified (2026-06-14): current =
		///     {0x001B,0x01F8,0x01FA}, max = {0x0001,0x01F9,0x01FB} (see CharacterStatId). A regen/damage delta
		///     mutates the CURRENT value, so it must ride the current id - sending it under the max id (the old bug)
		///     grew the player's MAX bar instead of refilling current.
		/// </summary>
		public CharacterStatBlock ToVitalsBlock()
		{
			return new CharacterStatBlock()
				.Set(CharacterStatId.HealthCurrent, Health)
				.Set(CharacterStatId.HealthMax, MaxHealth)
				.Set(CharacterStatId.StaminaCurrent, Stamina)
				.Set(CharacterStatId.StaminaMax, MaxStamina)
				.Set(CharacterStatId.ManaCurrent, Mana)
				.Set(CharacterStatId.ManaMax, MaxMana);
		}

		/// <summary>
		///     Full-health vitals seeded from a character's persisted max stats (class/level defaults if it has none),
		///     PLUS the equipped-gear vital bonuses (#3) so worn +HP/+Stamina/+Mana raises the max and the regen bar can
		///     fill past the class base. The world model's single seed point for a player's live vitals - used both by
		///     combat and by the world simulation when a player is registered. <paramref name="gearBonuses" /> is the
		///     MaxStatAggregator output (display-unit totals by stat); null/absent = no gear bonus.
		/// </summary>
		public static EntityVitals FromCharacter(Character character, IReadOnlyDictionary<CharacterStatId, int>? gearBonuses = null,
			IReadOnlyDictionary<CharacterStatId, int>? classBase = null)
		{
			var max = character.MaxStats ?? CharacterMaxStats.CreateDefaults(character.Class, character.Level);

			// The client builds its max the same way and we can read its arithmetic straight off the ClientInterface
			// channel (#450). For a level-80 Necromancer it took OUR snapshot value as the base and added its own terms:
			//     634100 (our CharacterMaxStats, = 6341)  +  38600 class-feat HealthMax  +  58400 item HealthMax
			// The item term matched our gear aggregate exactly; the class-feat term is the #444 data we already store in
			// ClassAttributeBases and were not using, which is why the server clamped regen 386 low and the bar visibly
			// stalled short. Both terms are folded in here.
			//
			// The fourth term - buffs that raise a max pool (Focused Mind's +mana, Sustaining Wrath's +health) - is not
			// here because it is not fixed at world entry: BuffStatEffects re-applies it through SetMaxFromBonus on every
			// change to the entity's live buffs, on top of the base these two terms establish.
			var vitals = FromMax(AddGear(max.Health, classBase, CharacterStatId.HealthMax),
				AddGear(max.Mana, classBase, CharacterStatId.ManaMax),
				AddGear(max.Stamina, classBase, CharacterStatId.StaminaMax));
			vitals.BaseMaxHealth = vitals.MaxHealth;
			vitals.BaseMaxMana = vitals.MaxMana;
			vitals.BaseMaxStamina = vitals.MaxStamina;
			foreach (var stat in MaxPools)
				vitals.SetMaxFromBonus(stat, gearBonuses?.GetValueOrDefault(stat) ?? 0);

			vitals.ReviveToFull();
			// #214: a character that logged out dead is still dead - seed current health at 0 (max stays full) so the
			// world model agrees with the dead login snapshot and RespawnMe/TryRespawnNow (which gate on IsDead) work.
			if (character.IsDead)
				vitals.Health = 0;
			return vitals;
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

		// Adds the gear bonus for one vital stat onto its base max, clamped at 0 (a bonus never drops a pool below zero).
		private static uint AddGear(uint baseMax, IReadOnlyDictionary<CharacterStatId, int>? gearBonuses, CharacterStatId stat)
		{
			if (gearBonuses == null || !gearBonuses.TryGetValue(stat, out var bonus))
				return baseMax;

			var total = baseMax + bonus;
			return total < 0
				? 0
				: (uint)total;
		}

		private static uint ClampToMax(uint value, uint max)
		{
			return value > max
				? max
				: value;
		}
	}
}
