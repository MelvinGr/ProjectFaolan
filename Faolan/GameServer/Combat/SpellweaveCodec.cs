using Faolan.Core;
using Faolan.Core.Network;
using Faolan.Core.Network.Packets.Opcodes;

namespace Faolan.GameServer.Combat
{
	// Byte-exact builders for the spellweaving activate-response + fire wire (docs/combos.md, decoded in
	// Captures/decode_spellweave.py from NecroL80_..._consuming-potions-food-spellweaving; caster 0x0802E5D4).
	// Each method returns the record bytes AFTER the 0x2000 carrier's u32 length prefix - the caller wraps them in
	// the world-framed IIR carrier (WriteArrayUInt32Length), exactly like PetIirCodec.
	//
	// The three trance BUFFS (ApplySpell 0x37F29800 / 0x37E5A800 / 0x3969D000) are NOT built here: they go through
	// the existing ApplySpell helper (their DurationMs at data+8 already matches Spells.DurationMs byte-for-byte, so
	// that path reproduces them). This codec owns the two records that ApplySpell can't: the reactive element LIST
	// and the FIRE burst. The trance METER (0xE9F24184) drain-stream is a HUD gauge decoded in the script but not
	// built here yet (needs a trance-timer to stream it); it is not required to unblock the client's adds.
	public static class SpellweaveCodec
	{
		private const uint Type = EntityClass.Character; // the shared 0xC350 type word

		// The reactive element list (CombatReactiveIIR 0x54C02089): declares to the client which weave elements the
		// caster can ADD while in the trance - this is what lets the client send the 0x03-tagged adds. componentIndex
		// on an add indexes INTO this list. Record: [op][0xC350][caster][u32 0][u8 0x01][N x u32 elementBaseId].
		// elementBaseId = full spell id >> 8 (necro: Arcane Renewal 0x396AE9, Arcane Surge 0x3969D8, Parasite Host
		// 0x37E6D9, Death God 0x396A22, Death 0x396A3D). A per-add re-send zeroes the just-used element (cooldown);
		// pass 0 for a consumed slot to reproduce that. tag 0x01 is a constant "list active" flag.
		public static byte[] BuildReactiveList(uint caster, uint[] elementBaseIds)
		{
			var stream = new FaolanStream()
				.WriteUInt32((uint)GameServer0X2000RespondsOpcodes.CombatReactiveIIR)
				.WriteUInt32(Type)
				.WriteUInt32(caster)
				.WriteUInt32(0)
				.WriteByte(0x01);

			foreach (var element in elementBaseIds)
				stream.WriteUInt32(element);

			return stream.ToArray();
		}

		// One fire-burst stage (StatIIR 0xF98E10B3) emitted per stage on DEACTIVATE: the accumulated weave firing.
		// Record: [op][0xC350][caster][u8 0x01][u32 fireId][u32 0x00007BD4]. Necro fire ids: 0x398/0x39B/0x39A/0x39D.
		// The 0x7BD4 tail is constant across the captured burst.
		public static byte[] BuildFireStage(uint caster, uint fireId)
		{
			return new FaolanStream()
				.WriteUInt32((uint)GameServer0X2000RespondsOpcodes.StatIIR)
				.WriteUInt32(Type)
				.WriteUInt32(caster)
				.WriteByte(0x01)
				.WriteUInt32(fireId)
				.WriteUInt32(0x00007BD4)
				.ToArray();
		}
	}
}
