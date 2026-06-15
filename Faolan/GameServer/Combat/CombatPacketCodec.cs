using Faolan.Core.Network;
using System;
using System.Buffers.Binary;
using Faolan.Core.Data;
using Faolan.Core.Network.Packets.Opcodes;

namespace Faolan.GameServer.Combat
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
		public const uint DamageResultSubOpcode = (uint)GameServer0X2000RespondsOpcodes.DamageResultIIR;
		public const uint EnergySubOpcode = (uint)GameServer0X2000RespondsOpcodes.EnergyIIR;
		private const uint WireType = EntityClass.Character; // the C350 type word shared by the IIR record family

		public const int DamageResultBodyLength = 37;
		public const int EnergyBodyLength = 17;

		// The DamageResultIIR hit body - the floating number / health-bar move on `target` for `damage` from `attacker`.
		public static byte[] BuildDamageResultBody(uint attackerInstance, uint targetHandle, uint damage)
		{
			return new FaolanStream()
				.WriteUInt32(DamageResultSubOpcode)
				.WriteUInt32(WireType)
				.WriteUInt32(attackerInstance)
				.WriteByte(0x00)
				.WriteUInt32(WireType)
				.WriteUInt32(targetHandle)
				.WriteUInt32(damage)
				.WriteUInt32(2)
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
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(0, 4)) != DamageResultSubOpcode)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4, 4)) != WireType)
				return false;
			if (body[12] != 0x00)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(13, 4)) != WireType)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(25, 4)) != 2 ||
			    BinaryPrimitives.ReadUInt32BigEndian(body.Slice(29, 4)) != 0 ||
			    BinaryPrimitives.ReadUInt32BigEndian(body.Slice(33, 4)) != 0)
				return false;

			attackerInstance = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(8, 4));
			targetHandle = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(17, 4));
			damage = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(21, 4));
			return true;
		}

		// The EnergyIIR body - the actor's stamina after a swing (stamina-only in the captures).
		public static byte[] BuildEnergyBody(uint actorInstance, uint stamina)
		{
			return new FaolanStream()
				.WriteUInt32(EnergySubOpcode)
				.WriteUInt32(WireType)
				.WriteUInt32(actorInstance)
				.WriteByte(0x01)
				.WriteUInt32(stamina)
				.ToArray();
		}

		// The inverse of BuildEnergyBody. Validates the sub-opcode, the C350 type word and the 0x01 marker.
		public static bool TryParseEnergyBody(ReadOnlySpan<byte> body, out uint actorInstance, out uint stamina)
		{
			actorInstance = 0;
			stamina = 0;
			if (body.Length != EnergyBodyLength)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(0, 4)) != EnergySubOpcode)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4, 4)) != WireType)
				return false;
			if (body[12] != 0x01)
				return false;

			actorInstance = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(8, 4));
			stamina = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(13, 4));
			return true;
		}
	}
}
