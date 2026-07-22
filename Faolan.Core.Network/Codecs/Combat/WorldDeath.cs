using System.Numerics;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Combat
{
	// SimpleCharKilledByUpdateIIR (0xB066AFC2) - the explicit "this entity died" trigger. A health=0 StatUpdate
	// alone does NOT drive the client's death animation / dead (release-screen) state (docs/subsystems/combat.md, #204/#214);
	// the client only reacts when it also gets this kill record. Decoded from AssassinL23_..._resurect: the player-death
	// sequence right before the 0x2024 release screen carries this for the dying handle.
	//
	// Record (the IIR carrier prepends a u32 length): [subop][0000C350][dyingHandle][00][01][0000C350][killerEndpoint].
	// The trailing [00][01] + 0000C350 are constant; killerEndpoint is the slayer (ProtoEndpoint-style type<<16|instance);
	// 0 when unknown/self.
	public static class WorldDeath
	{
		public const byte NpcDeathVariant = 0x05;

		// The death-behavior BccIIR (0x642CD3D6) that transitions a dying SimpleChar - PLAYER or NPC - into the dead
		// state and plays its death animation. A form-0x0004 record on the dying handle, slot 0x1F, protobuf
		// [field1 = behaviorId][field3 = variant][field5 = 8][field6 = 0][field12 = 240], no tail. Retail sends it to:
		//   * the dying PLAYER (Stihetfv beach #1292, right after the death-effect ApplySpell) - drives the dead state
		//     (bcc_state 29). A login-while-dead char gets bcc_state=29 from its spawn, but an IN-SESSION death has no
		//     spawn re-send, so this is how the live SimpleChar transitions; behaviorId 41877, variant 0x03.
		//   * every dying NPC (NecroL80_..._casting-combat: EVERY kill = death-pose MoveTask(0x06) + this BccIIR, and NO
		//     SimpleCharKilledBy). The **behaviorId (field1) IS the creature's death animation** and is CREATURE-TYPE
		//     specific - decoded across the corpus (Faolan.Importer NpcDeathBehaviorExtractor, #362): 41877 = humanoid
		//     biped, 17036 = demon/large caster, 215333 = quadruped/beast. It's harvested per NpcTemplate
		//     (DeathBehaviorId) and passed in; un-harvested types default to the humanoid 41877. field3 (variant) and
		//     field12 vary per-kill (hit-direction / ragdoll-impulse / timing), NOT the anim identity, so they're fixed.
		public const byte PlayerDeathVariant = 0x03;
		public const uint DeathEffectSpellId = 0x0009F000;
		public const uint HumanoidDeathBehaviorId = 41877; // the default biped death; field1 varint 0x08 0x95 0xC7 0x02
		public const uint SimpleCharKilledByOpcode = (uint)GameServer0X2000Opcodes.SimpleCharKilledByUpdateIIR; // 0xB066AFC2
		private const byte DeadMovementType = 0x06;

		// The revive action retail sends on RespawnMe — a BehaviorControlCenterIIR (0x642CD3D6) on the player that
		// transitions bcc_state 29→1 (alive), which CLOSES the release window and revives in place. Decoded from the
		// retail beach death+resurrect (Stihetfv #1382, identical form in AssassinL23 #4840): a form-0x0004 BccIIR record
		// with a fixed protobuf (behaviorId 41368) and an 8-byte tail [u32 3][u32 respawnPointId]. Capture-derived
		// verbatim except the player handle + the chosen resurrection-point id. Retail follows it with a vitals
		// restore (we ride PushVitals) and an IntendedTeleport to the point (deferred - the beach point is adjacent so
		// we revive in place).
		private const uint BccIirOpcode = (uint)GameServer0X2000Opcodes.BehaviorControlCenterIIR; // 0x642CD3D6

		// The self-applied "you are dead" effect retail sends right after the 0x2024 release screen (ApplySpell
		// MagicBuff 0x6B87E316, spell 0x0009F000, applyTo == applyFrom == the dying player). SimpleCharKilledBy +
		// the resurrection screen alone left the client alive in-client (#214); this effect is the piece that drives
		// the dead/release visual state. Decoded from AssassinL23_..._resurect pkt#4051: applyTo/applyFrom rewritten to
		// the live handle, payload kept verbatim (capture-derived interim, like the starting shield buff - the
		// effect's magnitude/duration fields aren't decoded yet).
		private const uint DeathEffectOpcode = (uint)GameServer0X2000Opcodes.MagicBuffIIR; // 0x6B87E316 - ApplySpell / MagicBuff

		// The death-pose MoveTask retail forces onto the dying PLAYER's own handle right before the kill record
		// (AssassinL23_..._resurect pkt#4048): a minimal MoveTask (0x96C46740) with movement-type 0x06 and NO waypoint/
		// speed payload - just [class][handle][u16 0][pos X,Y,Z big-endian][0x06] - at the death position. Normal
		// movement is type 0x07 followed by a full waypoint list; 0x06 is the "stopped/incapacitated" dead state. The
		// SimpleCharKilledBy + 0x2024 + death effect alone left the avatar standing and the release UI closed (#214).
		private const uint MoveTaskOpcode = (uint)GameServer0X2000Opcodes.MoveTaskIIR; // 0x96C46740

		// The death sequence is four distinct IIR records, so this builder declares the SET it emits rather than a
		// single Opcode (same contract as DynelFullUpdateCodec/SimpleDynelMessageCodec).
		public static readonly GameServer0X2000Opcodes[] HandledTypes =
		[
			GameServer0X2000Opcodes.SimpleCharKilledByUpdateIIR, GameServer0X2000Opcodes.BehaviorControlCenterIIR, GameServer0X2000Opcodes.MagicBuffIIR, GameServer0X2000Opcodes.MoveTaskIIR
		];

		private static readonly byte[] DeathEffectPayload =
		[
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0xBF, 0x20, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x09, 0xF0, 0x00, 0x00, 0x00, 0x17,
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x17, 0x00, 0x00, 0x00, 0x00, 0x14, 0xEF, 0x17, 0x7D
		];

		// The revive BCC's args (was the literal [0x08,0x98,0xC3,0x02, 0x18,0x04, 0x28,0x08, 0x30,0x00, 0x60,0x64]).
		private static readonly byte[] ReviveBccProtobuf = new CommandMessage
		{
			BccActionId = 41368, // 0x98 0xC3 0x02
			CommandqueueServerId = 4,
			Flags = 8,
			ExecutionStatus = 0,
			ActivationTime = 100
		}.ToProtobuf();

		public static byte[] BuildDeathBcc(uint handle, uint deathBehaviorId = HumanoidDeathBehaviorId, byte deathVariant = PlayerDeathVariant)
		{
			// protobuf: field1(0x08) = behaviorId varint (the creature's death animation), field3(0x18) = variant,
			// field5(0x28)=8, field6(0x30)=0, field12(0x60) = 240 varint (0xF0 0x01).
			var protobuf = new CommandMessage
			{
				BccActionId = deathBehaviorId, // the creature's death animation
				CommandqueueServerId = deathVariant,
				Flags = 8,
				ExecutionStatus = 0,
				ActivationTime = 240
			}.ToProtobuf();
			return new FaolanStream()
				.WriteUInt32(BccIirOpcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(handle)
				.WriteUInt16(0x0004) // form
				.WriteUInt32(0x0000001F) // slot
				.WriteArrayUInt32Length(protobuf)
				.ToArray();
		}

		public static byte[] BuildDeathEffect(uint dyingHandle)
		{
			return new FaolanStream()
				.WriteUInt32(DeathEffectOpcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(dyingHandle) // applyTo = the dying player
				.WriteUInt16(0x0000)
				.WriteUInt32(DeathEffectSpellId)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(dyingHandle) // applyFrom = self
				.WriteArray(DeathEffectPayload)
				.ToArray();
		}

		public static byte[] BuildDeathPose(uint dyingHandle, Vector3 position)
		{
			return new FaolanStream()
				.WriteUInt32(MoveTaskOpcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(dyingHandle)
				.WriteUInt16(0x0000)
				.WriteVector3(position)
				.WriteByte(DeadMovementType)
				.ToArray();
		}

		public static byte[] BuildKilledBy(uint dyingHandle, uint killerEndpoint)
		{
			return new FaolanStream()
				.WriteUInt32(SimpleCharKilledByOpcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(dyingHandle)
				.WriteByte(0x00)
				.WriteByte(0x01)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(killerEndpoint)
				.ToArray();
		}

		public static byte[] BuildRevive(uint handle, uint respawnPointId)
		{
			return new FaolanStream()
				.WriteUInt32(BccIirOpcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(handle)
				.WriteUInt16(0x0004) // form
				.WriteUInt32(0x00000025) // slot
				.WriteArrayUInt32Length(ReviveBccProtobuf)
				.WriteUInt32(0x00000003) // tail prefix
				.WriteUInt32(respawnPointId) // the chosen resurrection-point id
				.ToArray();
		}
	}
}
