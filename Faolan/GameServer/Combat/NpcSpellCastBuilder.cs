using Faolan.Core;
using Faolan.Core.Network;
using Faolan.Core.Network.Packets.Opcodes;

namespace Faolan.GameServer.Combat
{
	// Builds the ApplySpell (0x6B87E316) carrier content for an NPC casting a spell at the player - the wire piece
	// of NPC spell-casting (#232). It is the SAME ApplySpell shape the player's self-buffs / offensive casts use,
	// only with applyFrom = the casting NPC's handle and applyTo = the target player. Decoded from the captures:
	// retail NPC casts are exactly these (28 caster NPCs on the beach, applyFrom = the NPC's spawn handle).
	//
	// The 54-byte effect `payload` is DB-sourced (NpcSpell.Payload) and now DECODED (diffed across all 21 distinct
	// captured payloads, zero unexplained bytes - Captures/scripts one-off): byte-packed, big-endian:
	//   @10 u16 = DURATION (wire unit; 100..4000)   @13/@17/@33 = effect flags (0/1, 1/2, 0/1)
	//   @23 u32 = the effect SPELL ID (== the cast spell id, bar one capture-data mismatch)
	//   @29 + @45 bytes = the two magnitude/rank values (equal on most casts = the rank; differ on a few = a
	//                     separate magnitude), @50 u32 = a per-cast counter/timestamp the client does NOT validate.
	// Still carried as the stored blob here (the client renders it as-is); modelling it as NpcSpell columns +
	// building from them is the remaining #304 step, but the grammar is no longer opaque.
	//
	// Content layout (inside the 0x2000 IIR carrier, i.e. after its [u32 length]):
	//   [u32 subop=ApplySpell][u32 c350][u32 applyTo=player][u16 0][u32 spellId][u32 c350][u32 applyFrom=npc][payload]
	// Byte-exact against a captured NPC cast (NpcSpellCastBuilderTests / Golden NpcSpellCast.bin).
	public static class NpcSpellCastBuilder
	{
		public static byte[] Build(uint applyTo, uint applyFrom, uint spellId, byte[] payload)
		{
			return new FaolanStream()
				.WriteUInt32(GameServer0X2000RespondsOpcodes.MagicBuffIIR)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(applyTo)
				.WriteUInt16(0)
				.WriteUInt32(spellId)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(applyFrom)
				.WriteArray(payload ?? [])
				.ToArray();
		}
	}
}
