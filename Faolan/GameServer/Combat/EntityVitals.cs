using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.GameServer.FullCharSnapshot;

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
		///     Driven by the game loop (docs/archive/server-architecture-roadmap.md).
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

		/// <summary>Restores all three pools to their max (a full revive, e.g. on respawn).</summary>
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
		///     Full-health vitals seeded from a character's persisted max stats (class/level defaults if it has none).
		///     The world model's single seed point for a player's live vitals - used both by combat and by the world
		///     simulation when a player is registered.
		/// </summary>
		public static EntityVitals FromCharacter(Character character)
		{
			var max = character.MaxStats ?? CharacterMaxStats.CreateDefaults(character.Class, character.Level);
			var vitals = FromMax(max.Health, max.Mana, max.Stamina);
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

		private static uint ClampToMax(uint value, uint max)
		{
			return value > max
				? max
				: value;
		}
	}
}
