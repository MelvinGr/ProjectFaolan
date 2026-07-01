using Faolan.Core;
using Faolan.Core.Network;
using Faolan.Core.Network.Packets.Opcodes;

namespace Faolan.GameServer.Combat
{
	// Builds the ApplySpell (0x6B87E316) carrier content for an NPC casting a spell at the player - the wire piece
	// of NPC spell-casting (#232). It is the SAME ApplySpell shape the player's self-buffs / offensive casts use,
	// only with applyFrom = the casting NPC's handle and applyTo = the target player. Decoded from the captures:
	// retail NPC casts are exactly these (28 caster NPCs on the beach, applyFrom = the NPC's spawn handle). The
	// effect `payload` (magnitude/duration) is carried verbatim from the NpcSpell row for now - undecoded, like the
	// self-buff path - so the client renders the captured effect.
	//
	// Content layout (inside the 0x2000 IIR carrier, i.e. after its [u32 length]):
	//   [u32 subop=ApplySpell][u32 c350][u32 applyTo=player][u16 0][u32 spellId][u32 c350][u32 applyFrom=npc][payload]
	// Byte-exact against a captured NPC cast (NpcSpellCastBuilderTests / Golden NpcSpellCast.bin).
	public static class NpcSpellCastBuilder
	{
		public static byte[] Build(uint applyTo, uint applyFrom, uint spellId, byte[] payload)
		{
			return new FaolanStream()
				.WriteUInt32(GameServer0X2000RespondsOpcodes.ApplySpell)
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
