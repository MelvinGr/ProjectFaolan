using Faolan.Core.Network.Protobuf;

namespace Faolan.GameServer.Spawn
{
	// Builds the BASE player-spawn ProtoCoreCharacterIIR from code - the message PlayerSpawnMessage.ApplyCharacter then
	// overrides per character (name / identity / looks / worn gear). Formerly the captured PlayerCharacterIirTemplate.bin
	// blob (Strie's 469B message); now constructed so the runtime ships no spawn byte-blob (#258 follow-up). The values
	// are Strie's captured template: the fixed render defaults (Flags/Radius/MountScale/Bcc/OrientationMode/MovementMode/
	// CanFall, the player_stats + idle-anim) plus the identity/looks the per-character override falls back to when the
	// DB has no value. Byte-exact to the template MINUS its worn-gear entries (PlayerCharacterIirTests) - those 4 entries
	// are always replaced by ApplyCharacter (every character carries buildable gear), so they are not reproduced here.
	public static class PlayerCharacterIir
	{
		public static ProtoCoreCharacterIIR BuildBaseMessage()
		{
			return new ProtoCoreCharacterIIR
			{
				// Name is always overridden by ApplyCharacter; kept as the template's value for the from-template parity.
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
		}
	}
}
