using Faolan.Core;
using Faolan.Core.Network;
using Faolan.Core.Network.Opcodes;

namespace Faolan.Core.Network.Codecs
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
		public static byte[] Build(uint applyTo, uint applyFrom, uint spellId, byte[] payload) => new NpcSpellCast(applyTo, applyFrom, spellId, payload ?? []).ToArray();

		// The ApplySpell NPC-cast carrier content as a self-serializing codec (ICodec): Write frames the fixed header
		// then the opaque 54-byte effect payload; Read recovers (applyTo, applyFrom, spellId, payload).
		public sealed record NpcSpellCast(uint ApplyTo, uint ApplyFrom, uint SpellId, byte[] Payload) : ICodec<NpcSpellCast>
		{
			public byte[] ToArray()
			{
				using var s = new FaolanStream();
				Write(s);
				return s.ToArray();
			}

			public void Write(FaolanStream s)
			{
				s.WriteUInt32(GameServer0X2000Opcodes.MagicBuffIIR)
					.WriteUInt32(EntityClass.Character)
					.WriteUInt32(ApplyTo)
					.WriteUInt16(0)
					.WriteUInt32(SpellId)
					.WriteUInt32(EntityClass.Character)
					.WriteUInt32(ApplyFrom)
					.WriteArray(Payload);
			}

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
		}
	}
}
