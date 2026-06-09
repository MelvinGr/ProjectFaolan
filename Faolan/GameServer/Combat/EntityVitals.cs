using Faolan.GameServer.Packets._0x737A6DF9_Test;

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

		/// <summary>
		///     The vitals as a <c>0x96B8DC59</c> stat block, emitting the current AND max id for each of
		///     Health/Stamina/Mana (the retail pairing - see docs/player-stats.md). Previously this sent the current
		///     value under the MAX id, so a damaged entity reported max == currentHP and the health bar never moved.
		/// </summary>
		public CharacterStatBlock ToVitalsBlock()
		{
			return new CharacterStatBlock()
				.Set(LegacyCharacterStatId.HealthCurrent, Health).Set(LegacyCharacterStatId.HealthMax, MaxHealth)
				.Set(LegacyCharacterStatId.StaminaCurrent, Stamina).Set(LegacyCharacterStatId.StaminaMax, MaxStamina)
				.Set(LegacyCharacterStatId.ManaCurrent, Mana).Set(LegacyCharacterStatId.ManaMax, MaxMana);
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
	}
}
