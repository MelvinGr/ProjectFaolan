using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Protobuf;

namespace Faolan.GameServer.Spawn
{
	// Applies a character's DB fields onto a player-spawn ProtoCoreCharacterIIR (identity / appearance / worn gear),
	// keeping the rest of the template message. Shared by the live splice path (RetailLoginReplay, template = the
	// captured burst's player message) and the burst-less path (#210, template = a server-owned CharacterIIR), so
	// both produce the same per-character overrides from one place.
	public static class PlayerSpawnMessage
	{
		// CharacterStats.bcc_state value for a dead character (alive==1). The behavior-controller Dead state enum.
		private const uint DeadBccState = 29;

		// The forced "death" animation id retail sets in a dead player's BCC (Stihetfv login-while-dead FA015807).
		// Capture-derived (conqueror); may be per-skeleton - see the call site.
		private const uint DeathForcedAnimId = 41877;

		// Mutates `message` in place: name, identity (sex/race/level + cur==max vitals), editor looks, worn equipment.
		// Fields with no DB value (race/sex 0, no Body, no buildable gear) keep the template's captured value.
		public static void ApplyCharacter(ProtoCoreCharacterIIR message, Character character, CharacterEquipment equipment)
		{
			message.Name = character.Name;

			// Override the player's identity (model) from the DB when set; otherwise keep the captured stats
			// (a race/sex of 0 would be an invalid model). The remaining CharacterStats fields stay captured.
			if (message.Stats != null && (uint)character.Race != 0)
			{
				// CharacterStats.race == the Race enum (Aquilonian=1, Cimmerian=2, Stygian=3); the spawn's sex
				// code is the Sex enum + 2 (MALE=2 / FEMALE=3 - the editor's ChangeSex 4/6 halved, the same
				// 2x convention as race/class; anchored on Hoxxling, who is male: editor 4 -> wire 2 and a
				// chosen beardmesh).
				message.Stats.Sex = (uint)character.Sex + 2;
				message.Stats.Race = (uint)character.Race;
				message.Stats.Level = character.Level;

				// Vitals (cur==max) from the persisted MaxStats, on the ×100 wire scale (Strie's life 9000 = 90).
				// A DEAD character logs in with CURRENT health = 0 (life/max stays full): this FA015807 is the
				// SimpleCharFullUpdateIIR that CONSTRUCTS the local SimpleChar whose dead-flag gates the
				// release/resurrect window (DAT_0297d374->[0x10]). The flag is latched at construction from a 0
				// current-health, NOT from the 0x737A6DF9 snapshot (a different object) - which is why zeroing only
				// the snapshot's HealthCurrent (#214 step 2) left the avatar standing. Verified against the retail
				// login-while-dead capture (Stihetfv FA015807 #50): CharacterStats.health(f7)=0, life(f6)=19600 full,
				// and can_fall(f20)=0 (alive=1). docs/handoff-2026-06-20-death-window.md.
				var maxStats = character.MaxStats;
				if (maxStats != null)
				{
					message.Stats.Life = maxStats.Health * 100;
					message.Stats.Health = character.IsDead
						? 0
						: maxStats.Health * 100;
					message.Stats.MaxStamina = maxStats.Stamina * 100;
					message.Stats.Stamina = maxStats.Stamina * 100;
					message.Stats.MaxMana = maxStats.Mana * 100;
					message.Stats.Mana = maxStats.Mana * 100;
				}
				// Remaining CharacterStats fields (looks_package, side, ...) stay captured.

				// THE Dead-substate gate: CharacterStats.bcc_state (the behavior/state-controller state enum).
				// Across all 15 captured ALIVE player spawns bcc_state==1; the ONLY spawn that carries DeadBccState
				// (29) is the retail login-while-dead capture. Health=0 and the forced death animation are both
				// COSMETIC (HP bar / lying-down pose - the avatar could still walk in-client #214); bcc_state is what
				// locks the controller into the Dead substate and lets the resurrect window paint. Set it for a dead
				// login (kept captured == alive 1 otherwise). docs/handoff-2026-06-20-death-window.md.
				if (character.IsDead)
					message.Stats.BccState = DeadBccState;

				// Retail's dead SimpleChar also reports can_fall=0 (alive=1) - a corpse is ragdoll/incapacitated, not
				// gravity-stepped. Match it for a dead login (kept captured when alive).
				if (character.IsDead)
					message.CanFall = false;
			}

			// The rendered looks from the editor choices (CharacterBody). The editor reports head mesh /
			// voice / size as 2x the wire value - the same convention as race/class - so: head_mesh =
			// ChangeHeadMesh/2, voice_set = ChangeVoice/2, monster_scale = ChangeSize/2 (verified on all
			// five create captures, e.g. dikketest 2285694 -> 1142847, voice 10 -> 5, size 194 -> 97).
			// Unset values (no editor choice) keep the captured field.
			if (message.Stats != null && character.Body != null)
			{
				if (character.Body.HeadMesh != 0)
					message.Stats.HeadMesh = character.Body.HeadMesh / 2;
				if (character.Body.Voice != 0)
					message.Stats.VoiceSet = character.Body.Voice / 2u;
				if (character.Body.Size != 0)
					message.Stats.MonsterScale = character.Body.Size / 2u;
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
			// just needs a per-skeleton lookup. docs/handoff-2026-06-20-death-window.md.
			if (character.IsDead && message.Bcc != null)
			{
				message.Bcc.AnimForcedId = DeathForcedAnimId;
				message.Bcc.AnimForcedTimer = 1.0f;
				message.Bcc.AnimForcedBlend = true;
			}
		}
	}
}
