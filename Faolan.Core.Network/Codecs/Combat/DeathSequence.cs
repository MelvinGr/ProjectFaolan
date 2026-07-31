using System.Numerics;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Combat
{
	// The records retail sends around a death, as VALUE factories.
	//
	// Not a codec, and that is the point of the file: a death is FIVE different messages
	// (SimpleCharKilledByUpdateIIR + BehaviorControlCenterIIR + MagicBuffIIR + LookAtIIR + MoveTaskIIR), and the
	// builder this replaced declared all five as its own `HandledTypes` - re-implementing four records that already had
	// codecs, under four more names. Wave 4 of #530 kept `SimpleCharKilledByUpdateCodec` (the one IIR that is genuinely
	// this file's) and turned the rest into values the existing codecs encode. Same treatment `ConversationScreenDecoder`
	// got in wave 2, for the same reason: orchestrating a sequence is not encoding a message.
	//
	// The sequence retail sends for a dying entity, in order: drop the target (`LookAts.DropTarget`), the death pose
	// (`Pose`), the death behaviour (`Bcc`), and - for a player - the kill record
	// (`SimpleCharKilledByUpdateCodec.Build`) then the 0x2024 release screen.
	public static class DeathSequence
	{
		// The death-behavior variant for an NPC kill (the `deathVariant` field3 of the BCC args).
		public const byte NpcDeathVariant = 0x05;

		// ...and for a player death (Stihetfv beach #1292).
		public const byte PlayerDeathVariant = 0x03;

		// The **behaviorId IS the creature's death animation** and is CREATURE-TYPE specific - decoded across the corpus
		// (Faolan.Importer NpcDeathBehaviorExtractor, #362): 41877 = humanoid biped, 17036 = demon/large caster,
		// 215333 = quadruped/beast. It is harvested per NpcTemplate (DeathBehaviorId) and passed in; un-harvested types
		// default to the humanoid one.
		public const uint HumanoidDeathBehaviorId = 41877;

		// The self-applied "you are dead" effect's spell id.
		public const uint DeathEffectSpellId = 0x0009F000;

		// The BCC slot (behaviorId) of the death transition and of the revive.
		private const uint DeathSlot = 0x0000001F;
		private const uint ReviveSlot = 0x00000025;

		// The BCC seq (form) both carry.
		private const ushort DeathSeq = 0x0004;

		// The dead-state movement type: normal movement is a mode-07 path, 0x06 is the "stopped/incapacitated" pose.
		private const byte ReviveTailPrefix = 0x03;

		// The revive's args (was the literal [0x08,0x98,0xC3,0x02, 0x18,0x04, 0x28,0x08, 0x30,0x00, 0x60,0x64]).
		private static readonly byte[] ReviveArgs = new CommandMessage
		{
			BccActionId = 41368, // 0x98 0xC3 0x02
			CommandqueueServerId = 4,
			Flags = 8,
			ExecutionStatus = 0,
			ActivationTime = 100
		}.ToProtobuf();

		// The death-behavior record that transitions a dying SimpleChar - PLAYER or NPC - into the dead state and plays
		// its death animation. Retail sends it to:
		//   * the dying PLAYER (Stihetfv beach #1292, right after the death-effect ApplySpell) - drives the dead state
		//     (bcc_state 29). A login-while-dead char gets bcc_state=29 from its spawn, but an IN-SESSION death has no
		//     spawn re-send, so this is how the live SimpleChar transitions.
		//   * every dying NPC (NecroL80_..._casting-combat: EVERY kill = death-pose MoveTask + this record, and NO
		//     SimpleCharKilledBy).
		// field3 (variant) and field12 vary per-kill (hit-direction / ragdoll-impulse / timing), NOT the anim identity,
		// so they are fixed here.
		public static BehaviorControlCenterIIR Bcc(uint handle, uint deathBehaviorId = HumanoidDeathBehaviorId, byte deathVariant = PlayerDeathVariant)
		{
			var args = new CommandMessage
			{
				BccActionId = deathBehaviorId, // the creature's death animation
				CommandqueueServerId = deathVariant,
				Flags = 8,
				ExecutionStatus = 0,
				ActivationTime = 240
			}.ToProtobuf();

			return new BehaviorControlCenterIIR(handle, DeathSeq, DeathSlot, args, []);
		}

		// ⚠️ DECODED BUT DELIBERATELY NOT SENT. Retail's AssassinL23 (map 2010) death carries this self-applied
		// death-state effect, but the retail BEACH death does NOT (proven by live gdb 2026-06-20), and the spell's
		// client RDB effect sets stat 0x546 - the WAVE-RESPAWN timer - to 15. Sending it puts the client into the
		// dreaded wave countdown, which is exactly the #214 bug the death sequence was fixed to avoid. The factory and
		// its golden test stay because the bytes are proven and the record is part of the decoded death grammar; the
		// IWorldOutbound seam that used to expose it was removed, so there is no longer a one-call path to the
		// regression. If a capture ever justifies sending it, gate it on the map/death kind, not on "retail has it".
		//
		// applyTo == applyFrom == the dying player. Decoded from AssassinL23_..._resurect pkt#4051: the handles are
		// rewritten to the live one and the data region kept verbatim (capture-derived interim - the effect's
		// magnitude/duration fields are not decoded).
		public static MagicBuffIIR Effect(uint dyingHandle)
		{
			return new MagicBuffIIR(dyingHandle, 0x00, MagicBuffIds.BaseOf(DeathEffectSpellId), MagicBuffIds.FormOf(DeathEffectSpellId), dyingHandle, EffectData);
		}

		// The death-pose MoveTask retail forces onto the dying entity's own handle right before the kill record
		// (AssassinL23_..._resurect pkt#4048): a minimal stationary MoveTask at the death position, with no
		// waypoint/speed payload. The SimpleCharKilledBy + 0x2024 + death effect alone left the avatar standing and the
		// release UI closed (#214).
		public static MoveTaskIIR Pose(uint dyingHandle, Vector3 position)
		{
			return new MoveTaskIIR(dyingHandle, position, MoveTaskNodes.Stationary());
		}

		// The revive action retail sends on RespawnMe - a behaviour record on the player that transitions bcc_state
		// 29->1 (alive), which CLOSES the release window and revives in place. Decoded from the retail beach
		// death+resurrect (Stihetfv #1382, identical form in AssassinL23 #4840). Capture-derived verbatim except the
		// player handle + the chosen resurrection-point id. Retail follows it with a vitals restore (we ride
		// PushVitals) and an IntendedTeleport to the point (deferred - the beach point is adjacent so we revive in place).
		public static BehaviorControlCenterIIR Revive(uint handle, uint respawnPointId)
		{
			using var tail = new FaolanStream();
			tail.WriteUInt32(ReviveTailPrefix).WriteUInt32(respawnPointId);
			return new BehaviorControlCenterIIR(handle, DeathSeq, ReviveSlot, ReviveArgs, tail.ToArray());
		}

		private static readonly byte[] EffectData =
		[
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0xBF, 0x20, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x09, 0xF0, 0x00, 0x00, 0x00, 0x17,
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x17, 0x00, 0x00, 0x00, 0x00, 0x14, 0xEF, 0x17, 0x7D
		];
	}
}
