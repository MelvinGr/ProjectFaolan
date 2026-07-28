using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// Builds the ApplySpell (0x6B87E316) carrier content for an NPC casting a spell at the player - the wire piece
	// of NPC spell-casting (#232). It is the SAME ApplySpell shape the player's self-buffs / offensive casts use,
	// only with applyFrom = the casting NPC's handle and applyTo = the target player. Decoded from the captures:
	// retail NPC casts are exactly these (28 caster NPCs on the beach, applyFrom = the NPC's spawn handle).
	//
	// The 54-byte effect `payload` is DB-sourced (NpcSpell.Payload) and DECODED. Its field NAMES now come from the
	// client's own MagicBuffIIR_c::Activate log format rather than from diffing captures (#438), which reframed
	// several of them - full table and the old-to-new mapping in docs/subsystems/npc-spell-applyspell.md:
	//   @0  Counter   @4 CounterType   @8 u32 DURATION (centiseconds; the empirical "@10 u16" is its low half)
	//   @12/@13 two bools, @14..41 a 7-word descriptor (slot[1] = @22 = the spell BASE id, slot[3] = @26)
	//   @42 u32 CastingLevel (the caster's level - the empirical "@45 byte Rank" is its low byte)
	//   @46 u64 an absolute EXPIRY stamp (the empirical "@50 u32 counter" is its LOW HALF, not a field)
	// Still carried as the stored blob here (the client renders it as-is); modelling it as NpcSpell columns +
	// building from them is the remaining #304 step, but the grammar is no longer opaque.
	//
	// Content layout (inside the 0x2000 IIR carrier, i.e. after its [u32 length]):
	//   [u32 subop=ApplySpell][u32 c350][u32 applyTo=player][u16 0][u32 spellId][u32 c350][u32 applyFrom=npc][payload]
	// Byte-exact against a captured NPC cast (NpcSpellCastBuilderTests / Golden NpcSpellCast.bin).
	public static class NpcSpellCastBuilder
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.MagicBuffIIR;

		public static byte[] Build(uint applyTo, uint applyFrom, uint spellId, byte[] payload)
		{
			return new NpcSpellCast(applyTo, applyFrom, spellId, payload ?? []).ToArray();
		}

		// The ApplySpell NPC-cast carrier content as a self-serializing codec (ICodec): Write frames the fixed header
		// then the opaque 54-byte effect payload; Read recovers (applyTo, applyFrom, spellId, payload).
		public sealed record NpcSpellCast(uint ApplyTo, uint ApplyFrom, uint SpellId, byte[] Payload) : ICodec<NpcSpellCast>
		{
			public static NpcSpellCast Read(FaolanStream s)
			{
				s.ReadUInt32(); // subop (MagicBuffIIR / ApplySpell)
				s.ReadUInt32(); // c350 marker
				var applyTo = s.ReadUInt32();
				s.ReadUInt16(); // 0
				var spellId = s.ReadUInt32();
				s.ReadUInt32(); // c350 marker
				var applyFrom = s.ReadUInt32();
				var payload = s.ReadRemainingArray();
				return new NpcSpellCast(applyTo, applyFrom, spellId, payload);
			}

			public void Write(FaolanStream s)
			{
				s
					.WriteUInt32(Opcode)
					.WriteUInt32(EntityClass.Character)
					.WriteUInt32(ApplyTo)
					.WriteUInt16(0)
					.WriteUInt32(SpellId)
					.WriteUInt32(EntityClass.Character)
					.WriteUInt32(ApplyFrom)
					.WriteArray(Payload);
			}
		}
	}
}
