using Faolan.Core.Database.Tables;
using Faolan.GameServer.Packets.FullCharSnapshot;

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
		///     Driven by the game loop (docs/server-architecture-roadmap.md).
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
		///     Health/Stamina/Mana (the retail pairing - see docs/player-stats.md). Previously this sent the current
		///     value under the MAX id, so a damaged entity reported max == currentHP and the health bar never moved.
		/// </summary>
		public CharacterStatBlock ToVitalsBlock()
		{
			return new CharacterStatBlock()
				.Set(LegacyCharacterStatId.HealthCurrent, Health)
				.Set(LegacyCharacterStatId.HealthMax, MaxHealth)
				.Set(LegacyCharacterStatId.StaminaCurrent, Stamina)
				.Set(LegacyCharacterStatId.StaminaMax, MaxStamina)
				.Set(LegacyCharacterStatId.ManaCurrent, Mana)
				.Set(LegacyCharacterStatId.ManaMax, MaxMana);
		}

		/// <summary>
		///     Full-health vitals seeded from a character's persisted max stats (class/level defaults if it has none).
		///     The world model's single seed point for a player's live vitals - used both by combat and by the world
		///     simulation when a player is registered.
		/// </summary>
		public static EntityVitals FromCharacter(Character character)
		{
			var max = character.MaxStats ?? CharacterMaxStats.CreateDefaults(character.Class, character.Level);
			return FromMax(max.Health, max.Mana, max.Stamina);
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
