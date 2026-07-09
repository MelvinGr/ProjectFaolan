using System;
using System.Buffers.Binary;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// The small fixed-layout combat result packets, decoupled from CombatService's damage/kill logic. Both bodies are
	// the inner 0x2000 IIR record (the bytes WriteArrayUInt32Length wraps) framed on the world conversation. The
	// layouts are capture-derived (strickenbla combat capture; see protocol_stats_and_combat) and were previously
	// built inline in two places each - this is the single Build/Parse codec for them (#89).
	//
	//   DamageResultIIR 0xFEC4158A: [subop][0000C350][attacker:4][00][0000C350][target:4][damage:4][02][00][00]  (37 b)
	//   EnergyIIR       0x40749B8A: [subop][0000C350][actor:4][01][stamina:4]                                     (17 b)
	public static class CombatPacketCodec
	{
		// The IIR types this codec constructs or parses (#317 convention; read by the coverage matrix).
		public static readonly GameServer0X2000Opcodes[] HandledTypes =
		[
			GameServer0X2000Opcodes.BehaviorControlCenterIIR,
			GameServer0X2000Opcodes.DamageResultIIR,
			GameServer0X2000Opcodes.EnergyIIR,
		];

		public const int DamageResultBodyLength = 37;
		public const int EnergyBodyLength = 17;

		// The NormalAttack behaviorId carried by an attack BehaviorControlCenterIIR.
		private const uint NormalAttackBehaviorId = 0x0000000F;

		// args.SpellTarget.field1 - the constant 0xC350 (50000) that precedes the target handle in the swing args.
		private const uint SpellTargetTypeWord = 0x0000C350;

		// NPC animation acks are server-initiated, so the echoed sequence is the constant 0x0004 (not a client seq).
		private const ushort NpcAttackAckSeq = 0x0004;

		// The DamageResultIIR hit body - the floating number / health-bar move on `target` for `damage` from `attacker`.
		public static byte[] BuildDamageResultBody(uint attackerInstance, uint targetHandle, uint damage)
		{
			return new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.DamageResultIIR)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(attackerInstance)
				.WriteByte(0x00)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(targetHandle)
				.WriteUInt32(damage)
				.WriteUInt32(2)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.ToArray();
		}

		// The EnergyIIR body - the actor's stamina after a swing (stamina-only in the captures).
		public static byte[] BuildEnergyBody(uint actorInstance, uint stamina)
		{
			return new FaolanStream().WriteUInt32(GameServer0X2000Opcodes.EnergyIIR).WriteUInt32(EntityClass.Character).WriteUInt32(actorInstance).WriteByte(0x01).WriteUInt32(stamina).ToArray();
		}

		// The NPC counter-attack ANIMATION ack: the "second BccAck with actor = target handle" (docs/subsystems/combat.md, was
		// undecoded). Structurally the SAME BehaviorControlCenterIIR (0x642CD3D6) as the player's targeted-hit attack
		// ack, but the roles are SWAPPED - top-level actor = the NPC, args.SpellTarget + the result-block target = the
		// player it hit - and the args carry NO Mode field (the player-only distinguisher). Sending it makes the client
		// play the NPC's swing animation + the player's hit-react. The damage here is the floating combat number; the
		// authoritative HP move still rides the world-tick StatUpdate (kept separate so we don't double-apply).
		// Decoded in Captures/scripts/decode_combat_npc_ack.py (98/98 frames); byte-exact against frame 6472 of the strickenbla
		// capture in CombatPacketCodecTests. The animation params (typeId/swingCounter/f5/f11/f12) + the result codes
		// (resultType/resultK1/resultK2) are not semantically decoded - capture-typical defaults for the live send,
		// the exact captured values for the golden test.
		public static byte[] BuildNpcAttackAnimationAckBody(uint npcHandle, uint playerHandle, uint damage, uint typeId, uint swingCounter, uint f5 = 24, uint f11 = 64, uint f12 = 56,
			byte resultType = 0xCB, uint resultK1 = 1, uint resultK2 = 2)
		{
			// args protobuf: {f1=TypeId, f3=swingCounter, f5, f6=0, f10=SpellTarget{f1=0xC350, f2=player}, f11, f12}.
			// NO f2/Mode - that field is present only on player-initiated actions and is the NPC-vs-player tell.
			var spellTarget = new FaolanStream().WriteByte(0x08).WriteVarint(SpellTargetTypeWord).WriteByte(0x10).WriteVarint(playerHandle).ToArray();
			var args = new FaolanStream()
				.WriteByte(0x08)
				.WriteVarint(typeId)
				.WriteByte(0x18)
				.WriteVarint(swingCounter)
				.WriteByte(0x28)
				.WriteVarint(f5)
				.WriteByte(0x30)
				.WriteVarint(0)
				.WriteByte(0x52)
				.WriteVarint((ulong)spellTarget.Length)
				.WriteArray(spellTarget)
				.WriteByte(0x58)
				.WriteVarint(f11)
				.WriteByte(0x60)
				.WriteVarint(f12)
				.ToArray();

			return new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.BehaviorControlCenterIIR)
				.WriteUInt32(SpellTargetTypeWord)
				.WriteUInt32(npcHandle)
				.WriteUInt16(NpcAttackAckSeq)
				.WriteUInt32(NormalAttackBehaviorId)
				.WriteUInt32(args.Length)
				.WriteArray(args)
				// result block (the single-entry 132-byte-frame layout)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(1) // [0][0][count=1]
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(0) // 6×u32 entry (zero in the single-hit case)
				.WriteUInt32(1)
				.WriteUInt32(SpellTargetTypeWord)
				.WriteUInt32(playerHandle)
				.WriteUInt32(0)
				.WriteByte(0x06)
				.WriteUInt32(resultType)
				.WriteUInt32(0)
				.WriteUInt32(resultK1)
				.WriteUInt32(damage)
				.WriteUInt32(resultK2)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.ToArray();
		}

		// The inverse of BuildDamageResultBody. Validates the sub-opcode, both C350 type words, the 0x00 marker and the
		// trailing [02][00][00] constants so a mis-routed body is rejected. Proven by the CombatPacketCodecTests round-trip.
		public static bool TryParseDamageResultBody(ReadOnlySpan<byte> body, out uint attackerInstance, out uint targetHandle, out uint damage)
		{
			attackerInstance = 0;
			targetHandle = 0;
			damage = 0;
			if (body.Length != DamageResultBodyLength)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(0, 4)) != (uint)GameServer0X2000Opcodes.DamageResultIIR)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4, 4)) != EntityClass.Character)
				return false;
			if (body[12] != 0x00)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(13, 4)) != EntityClass.Character)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(25, 4)) != 2 || BinaryPrimitives.ReadUInt32BigEndian(body.Slice(29, 4)) != 0 ||
			    BinaryPrimitives.ReadUInt32BigEndian(body.Slice(33, 4)) != 0)
				return false;

			attackerInstance = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(8, 4));
			targetHandle = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(17, 4));
			damage = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(21, 4));
			return true;
		}

		// The inverse of BuildEnergyBody. Validates the sub-opcode, the C350 type word and the 0x01 marker.
		public static bool TryParseEnergyBody(ReadOnlySpan<byte> body, out uint actorInstance, out uint stamina)
		{
			actorInstance = 0;
			stamina = 0;
			if (body.Length != EnergyBodyLength)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(0, 4)) != (uint)GameServer0X2000Opcodes.EnergyIIR)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4, 4)) != EntityClass.Character)
				return false;
			if (body[12] != 0x01)
				return false;

			actorInstance = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(8, 4));
			stamina = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(13, 4));
			return true;
		}
	}
}
