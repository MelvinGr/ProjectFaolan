using System;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// Builds the player-spawn ProtoCoreCharacterIIR from code + the character's DB fields. The message is a fixed
	// render template (Strie's captured PlayerCharacterIirTemplate.bin, now constructed so the runtime ships no spawn
	// byte-blob - #258) whose identity/looks/vitals/worn-gear are overridden per character. Pass character == null to
	// get the bare template (the golden oracle; PlayerCharacterIirTests proves it is byte-exact to the .bin minus the
	// worn-gear entries, which are always character-supplied).
	public static class PlayerCharacterIir
	{
		// CharacterStats.bcc_state value for a dead character (alive==1). The behavior-controller Dead state enum.
		private const uint DeadBccState = 29;

		// The forced "death" animation id retail sets in a dead player's BCC (Stihetfv login-while-dead FA015807).
		// Capture-derived (conqueror); may be per-skeleton - see the call site.
		private const uint DeathForcedAnimId = 41877;

		// The player-spawn message for `character` (worn `equipment` applied). character == null returns the bare
		// render template (fixed constants + Strie's identity/looks fallbacks, no worn gear).
		public static ProtoCoreCharacterIIR Build(Character character, CharacterEquipment equipment)
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
				ApplyCharacter(message, character, equipment);

			// GM Level: drive player_stats.gm_level from the account type (GM/Admin -> non-zero -> retail GM mode).
			// Only set when non-zero so the bare template (character == null) and normal players don't serialize the
			// field - keeps the byte-exact golden template intact. Account may be unloaded on a broadcast spawn -> 0.
			var gmLevel = character?.Account?.Type.ToClientGmLevel() ?? 0;
			if (gmLevel > 0)
				message.player_stats.GmLevel = gmLevel;

			return message;
		}

		// Overrides `message` in place from the character: name, identity (sex/race/level + cur==max vitals), editor
		// looks, worn equipment, and the dead-state signals. Fields with no DB value (race/sex 0, no Body, no buildable
		// gear) keep the template's captured value.
		private static void ApplyCharacter(ProtoCoreCharacterIIR message, Character character, CharacterEquipment equipment)
		{
			message.Name = character.Name;

			// Override the player's identity (model) from the DB when set; otherwise keep the captured stats
			// (a race/sex of 0 would be an invalid model). The remaining CharacterStats fields stay captured.
			// The identity ALWAYS applies. This used to be gated on `message.Stats != null && character.Race != 0`, so a
			// character with either missing kept the TEMPLATE's - meaning Strie's sex, race, LEVEL 1 and 9000/9800/23000
			// vitals shipped to every client that saw them, presented as that character. Neither is a state a character can
			// legitimately be in: every one is created with a race (CreateCharacter defaults it) and the template always
			// carries a stat block, so both are corruption and say so.
			var stats = message.Stats
				?? throw new InvalidOperationException($"The CharacterIIR template carries no CharacterStats, so {character.Name}'s identity cannot be applied.");
			if ((uint)character.Race == 0)
				throw new InvalidOperationException($"Character {character.Id:X8} ({character.Name}) has no race, so its spawn model cannot be built.");

			stats.Sex = (uint)character.Sex;
			stats.Race = (uint)character.Race;
			stats.Level = character.Level;

			// Vitals (cur==max) from the persisted MaxStats, on the ×100 wire scale (Strie's life 9000 = 90).
			// A DEAD character logs in with CURRENT health = 0 (life/max stays full): this FA015807 is the
			// SimpleCharFullUpdateIIR that CONSTRUCTS the local SimpleChar whose dead-flag gates the
			// release/resurrect window (DAT_0297d374->[0x10]). The flag is latched at construction from a 0
			// current-health, NOT from the 0x737A6DF9 snapshot (a different object) - which is why zeroing only
			// the snapshot's HealthCurrent (#214 step 2) left the avatar standing. Verified against the retail
			// login-while-dead capture (Stihetfv FA015807 #50): CharacterStats.health(f7)=0, life(f6)=19600 full,
			// and can_fall(f20)=0 (alive=1).
			var maxStats = character.MaxStats;
			if (maxStats != null)
			{
				stats.Life = maxStats.Health * 100;
				stats.Health = character.IsDead
					? 0
					: maxStats.Health * 100;
				stats.MaxStamina = maxStats.Stamina * 100;
				stats.Stamina = maxStats.Stamina * 100;
				stats.MaxMana = maxStats.Mana * 100;
				stats.Mana = maxStats.Mana * 100;
			}
			// Remaining CharacterStats fields (looks_package, side, ...) stay captured.

			// THE Dead-substate gate: CharacterStats.bcc_state (the behavior/state-controller state enum).
			// Across all 15 captured ALIVE player spawns bcc_state==1; the ONLY spawn that carries DeadBccState
			// (29) is the retail login-while-dead capture. Health=0 and the forced death animation are both
			// COSMETIC (HP bar / lying-down pose - the avatar could still walk in-client #214); bcc_state is what
			// locks the controller into the Dead substate and lets the resurrect window paint. Set it for a dead
			// login (kept captured == alive 1 otherwise).
			if (character.IsDead)
				stats.BccState = DeadBccState;

			// Retail's dead SimpleChar also reports can_fall=0 (alive=1) - a corpse is ragdoll/incapacitated, not
			// gravity-stepped. Match it for a dead login (kept captured when alive).
			if (character.IsDead)
				message.CanFall = false;

			// The rendered looks from the editor choices (CharacterBody). CharacterBody now stores the TRUE wire
			// values (the editor's doubled ChangeHeadMesh/Voice/Size are zigzag-decoded to their real value on read),
			// so they're emitted as-is (e.g. dikketest head_mesh 1142847, voice 5, size 97). Unset = keep captured.
			if (message.Stats != null && character.Body != null)
			{
				if (character.Body.HeadMesh != 0)
					message.Stats.HeadMesh = character.Body.HeadMesh;
				if (character.Body.Voice != 0)
					message.Stats.VoiceSet = character.Body.Voice;
				if (character.Body.Size != 0)
					message.Stats.MonsterScale = character.Body.Size;
			}

			// Replace the worn equipment with the character's own (when it has fully-buildable gear).
			var wornEquipment = PlayerSpawnEquipment.BuildInventoryEntries(equipment);
			if (wornEquipment.Count > 0)
			{
				message.InventoryEntries.Clear();
				foreach (var entry in wornEquipment)
					message.InventoryEntries.Add(entry);
			}

			// THE dead-state signal: a FORCED death animation in the BCC (animation controller). Health=0 alone only
			// blanks the HP bar (the avatar stays up, no release window - verified in-client #214); the client enters
			// the Dead substate / paints the resurrect window off the forced animation, NOT health. Across all 33
			// captured ALIVE player spawns the BCC carries NO anim_forced_* fields; the ONLY spawn that sets them is
			// the retail login-while-dead capture (Stihetfv FA015807 #50): anim_forced_id=41877, timer=1.0, blend=1.
			// Force it for a dead login (left unset when alive). The id is the conqueror capture's value - if the dead
			// POSE renders wrong on another class/skeleton but the WINDOW shows, the mechanism is confirmed and the id
			// just needs a per-skeleton lookup.
			if (character.IsDead && message.Bcc != null)
			{
				message.Bcc.AnimForcedId = DeathForcedAnimId;
				message.Bcc.AnimForcedTimer = 1.0f;
				message.Bcc.AnimForcedBlend = true;
			}
		}
	}
}
