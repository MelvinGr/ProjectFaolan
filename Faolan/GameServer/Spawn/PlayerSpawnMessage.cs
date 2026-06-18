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
				var maxStats = character.MaxStats;
				if (maxStats != null)
				{
					message.Stats.Life = maxStats.Health * 100;
					message.Stats.Health = maxStats.Health * 100;
					message.Stats.MaxStamina = maxStats.Stamina * 100;
					message.Stats.Stamina = maxStats.Stamina * 100;
					message.Stats.MaxMana = maxStats.Mana * 100;
					message.Stats.Mana = maxStats.Mana * 100;
				}
				// Remaining CharacterStats fields (looks_package, side, ...) stay captured.
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
		}
	}
}
