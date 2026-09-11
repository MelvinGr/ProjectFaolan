using System;
using System.Collections.Generic;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Spawn
{
	/// <summary>
	///     Builds the player-spawn ProtoCoreCharacterIIR from code plus the character's database fields. The message is a
	///     fixed render template, constructed rather than replayed, whose identity, looks, vitals and worn gear are
	///     overridden per character. Pass a null character to get the bare template - the golden oracle;
	///     PlayerCharacterIirTests proves it is byte-exact to the captured template apart from the worn-gear entries, which
	///     are always character-supplied.
	/// </summary>
	public static class PlayerCharacterIirs
	{
		/// <summary>
		///     The CharacterStats.bcc_state value for a dead character, from the behaviour-controller's state enum; alive is
		///     1.
		/// </summary>
		private const uint DeadBccState = 29;

		/// <summary>
		///     The forced death animation id retail sets in a dead player's BCC, from the Stihetfv login-while-dead capture.
		///     Capture-derived from a conqueror, and possibly per-skeleton; see the call site.
		/// </summary>
		private const uint DeathForcedAnimId = 41877;

		/// <summary>
		///     The player-spawn message for <paramref name="character" />, with their <paramref name="worn" /> item
		///     instances applied. A null character returns the bare render template: fixed constants plus the captured
		///     identity and looks fallbacks, and no worn gear.
		/// </summary>
		/// <remarks>
		///     <paramref name="maxVitals" /> is the character's live ceiling, the world entity's EntityVitals. The
		///     current-side vitals fields carry it, because only the live value knows the character's gear. It is ignored
		///     when the character is null.
		/// </remarks>
		public static ProtoCoreCharacterIIR ForCharacter(Character character, IReadOnlyDictionary<CharacterEquipmentSlot, ItemInstance>? worn, CharacterMaxStats maxVitals = null!)
		{
			var message = new ProtoCoreCharacterIIR
			{
				// Overridden below when a character is supplied; kept as the template's value for from-template parity.
				Name = "Strie",
				Stats = new ProtoCoreCharacterIIR.CharacterStats
				{
					// Identity/looks fallbacks (overridden from the DB when set): a rogue-ish Strie at L1.
					Sex = 3,
					Race = 1,
					Level = 1,
					ApprenticeLevel = 1,
					HeadMesh = 1139362,
					VoiceSet = 5,
					MonsterScale = 92,
					// cur==max vitals (overridden from CharacterMaxStats); Strie's life 9000 = 90 on the x100 wire scale.
					Life = 9000,
					Health = 9800,
					MaxStamina = 23000,
					Stamina = 23600,
					// Fixed render constants (never per-character): controller/state, mount scale, collision radius, flags.
					MountScale = 100,
					Flags = 1,
					Radius = 50,
					Bcc = 12488,
					BccState = 1,
					OrientationMode = 1,
					// Explicit zeros the captured template serializes (set so they go on the wire, matching it byte-for-byte).
					Side = 0,
					MonsterData = 0,
					MaxMana = 0,
					Mana = 0,
					LooksPackage = 0,
					MountFlags = 0,
					CarsGroup = 0
				},
				player_stats = new ProtoCoreCharacterIIR.PlayerStats
				{
					PlayerFlags = 1,
					Strength = 10,
					SubscriptionFlags = 769,
					HardcoreLevel = 0
				},
				Bcc = new ProtoCoreCharacterIIR.CharacterBcc
				{
					AnimIdleId = 42024,
					AnimIdleBlend = true,
					AnimIdleTimer = 0,
					AnimFlags = 0
				},
				CanFall = true,
				MovementMode = 2
			};

			if (character != null)
				ApplyCharacter(message, character, worn, maxVitals);

			// player_stats.gm_level comes from the account type: a GM or admin account gives a non-zero level, which is
			// retail's GM mode. It is set only when non-zero, so the bare template and normal players do not serialize
			// the field and the byte-exact golden template stays intact. The account may be unloaded on a broadcast
			// spawn, which reads as 0.
			var gmLevel = character?.Account?.ClientGmLevel() ?? 0;
			if (gmLevel > 0)
				message.player_stats.GmLevel = gmLevel;

			return message;
		}

		/// <summary>
		///     Overrides the message in place from the character: name, identity (sex, race, level and the current-equals-max
		///     vitals), editor looks, worn equipment and the dead-state signals. Fields with no database value - no Body, no
		///     buildable gear - keep the template's captured value.
		/// </summary>
		private static void ApplyCharacter(ProtoCoreCharacterIIR message, Character character, IReadOnlyDictionary<CharacterEquipmentSlot, ItemInstance>? worn, CharacterMaxStats maxVitals)
		{
			message.Name = character.Name;

			// The identity always applies, and both of its preconditions are corruption rather than states a character
			// can legitimately be in: every character is created with a race, and the template always carries a stat
			// block. Falling back to the template instead would ship the template character's sex, race, level and
			// vitals to every client that sees this one, presented as this character. The remaining CharacterStats
			// fields stay as captured.
			var stats = message.Stats
				?? throw new InvalidOperationException($"The CharacterIIR template carries no CharacterStats, so {character.Name}'s identity cannot be applied.");
			if ((uint)character.Race == 0)
				throw new InvalidOperationException($"Character {character.Id:X8} ({character.Name}) has no race, so its spawn model cannot be built.");

			stats.Sex = (uint)character.Sex;
			stats.Race = (uint)character.Race;
			stats.Level = character.Level;
			// ApprenticeLevel is the mentor system's effective level rather than a copy of Level, but retail keeps the
			// two equal for an un-mentored character and never omits it: all 17,717 captured spawns carry
			// apprentice_level, and it equals level in every one. The invariant matters twice over - the client divides
			// by it for armour mitigation, and the overhead nameplate renders max(Level, ApprenticeLevel). See
			// CharacterStatBlock.SetLevel.
			stats.ApprenticeLevel = character.Level;

			// Vitals from the persisted MaxStats, on the x100 wire scale.
			//
			// A dead character logs in with current health 0 while life and the other maxima stay full: this record is
			// the SimpleCharFullUpdateIIR that constructs the local SimpleChar whose dead flag gates the release and
			// resurrect window, and the flag is latched at construction from a zero current health rather than from the
			// snapshot, which is a different object. Verified against the retail login-while-dead capture (Stihetfv
			// packet 50): health 0, life 19600 full, can_fall 0.
			//
			// The max-side fields carry the class base, which the client tops up, and the current-side fields carry the
			// full value. Retail proves the split in one packet - a Conqueror at level 2 has life 19600, that curve's
			// class base, against current health 229. Sending the sheet value in the max slot makes the client top up
			// an already-topped-up number.
			var (baseHealth, baseMana, baseStamina) = CharacterMaxStats.ClassBaseHundredths(character.Class, character.Level);
			if (maxVitals != null)
			{
				stats.Life = baseHealth;
				stats.Health = character.IsDead
					? 0
					: maxVitals.Health * 100;
				stats.MaxStamina = baseStamina;
				stats.Stamina = maxVitals.Stamina * 100;
				stats.MaxMana = baseMana;
				stats.Mana = maxVitals.Mana * 100;
			}
			// Remaining CharacterStats fields (looks_package, side, ...) stay captured.

			// The dead-substate gate is CharacterStats.bcc_state, the behaviour/state-controller state enum. Across
			// all 15 captured alive player spawns it is 1, and the only spawn carrying 29 is the retail
			// login-while-dead capture. Health 0 and the forced death animation are cosmetic - the HP bar and the
			// lying-down pose, with the avatar still able to walk - while bcc_state is what locks the controller into
			// the Dead substate and lets the resurrect window paint.
			if (character.IsDead)
				stats.BccState = DeadBccState;

			// Retail's dead SimpleChar also reports can_fall 0, alive being 1: a corpse is ragdolled rather than
			// gravity-stepped.
			if (character.IsDead)
				message.CanFall = false;

			// The rendered looks from the editor choices. CharacterBody stores the true wire values - the editor's
			// doubled ChangeHeadMesh, Voice and Size are zigzag-decoded to their real value on read - so they are
			// emitted as-is. An unset value keeps the captured one.
			if (message.Stats != null && character.Body != null)
			{
				if (character.Body.HeadMesh != 0)
					message.Stats.HeadMesh = character.Body.HeadMesh;
				if (character.Body.Voice != 0)
					message.Stats.VoiceSet = character.Body.Voice;
				if (character.Body.Size != 0)
					message.Stats.MonsterScale = character.Body.Size;
			}

			// Replace the worn equipment with the character's own, when it has fully buildable gear.
			var wornEquipment = PlayerSpawnEquipment.BuildInventoryEntries(worn);
			if (wornEquipment.Count > 0)
			{
				message.InventoryEntries.Clear();
				foreach (var entry in wornEquipment)
					message.InventoryEntries.Add(entry);
			}

			// The other dead-state signal is a forced death animation in the BCC's animation controller. Health 0 alone
			// only blanks the HP bar, leaving the avatar standing with no release window; the client paints the
			// resurrect window off the forced animation rather than off health. Across all 33 captured alive player
			// spawns the BCC carries no anim_forced_* fields, and the only spawn that sets them is the retail
			// login-while-dead capture (anim_forced_id 41877, timer 1.0, blend 1). The id is that conqueror capture's
			// value: if the dead pose renders wrong on another skeleton but the window still shows, the mechanism is
			// confirmed and the id needs a per-skeleton lookup.
			if (character.IsDead && message.Bcc != null)
			{
				message.Bcc.AnimForcedId = DeathForcedAnimId;
				message.Bcc.AnimForcedTimer = 1.0f;
				message.Bcc.AnimForcedBlend = true;
			}
		}
	}
}
