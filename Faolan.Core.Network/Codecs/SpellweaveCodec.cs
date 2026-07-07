using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Opcodes;

namespace Faolan.Core.Network.Codecs
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
		public const uint DotGauge = 0x37E690; // "Spellweaving" -> the health DoT when stamina is empty
		public const uint DotScale = 0x0000001B; // v@33 for the DoT gauge

		// A spellweave stat-gauge tick (StatDamageIIR 0xE9F24184) - the client's draining gauge. Two are used: the
		// STAMINA gauge ("Spellweaving Cost" 0x37E68C, scale 0x0F425C) that drains while weaving, and, once stamina
		// empties, the health DoT gauge ("Spellweaving" 0x37E690, scale 0x1B) that damages the caster. `token` is the
		// current bar value (drains toward 0), `delta` the signed change since the last tick; `scale` is the gauge's
		// per-type constant (v@33). mode 2, magnitude 80 are shared constants. Byte-exact vs frames 1654 (stamina) and
		// the DoT frame (0x37E690) of the cancel-weave/DoT capture. Record: [op][0xC350][caster][u8 0][u32 token][0][0]
		// [0][u32 scale][u32 mode=2][i32 delta][0][0xC350][caster][u32 1][0][u32 gaugeId][u32 magnitude=80][0][0][0].
		public const uint StaminaGauge = 0x37E68C; // "Spellweaving Cost" -> the yellow stamina bar
		public const uint StaminaScale = 0x000F425C; // v@33 for the stamina gauge
		private const uint GaugeMagnitude = 80;
		private const uint Type = EntityClass.Character; // the shared 0xC350 type word

		// The 54-byte MagicBuffIIR (0x6B87E316) DATA tail for a spellweave buff - the bytes AFTER the ApplySpell
		// header ([op][0xC350][applyTo][u16 0][spellId][0xC350][applyFrom]). Constructed field-by-field (decoded from
		// the trance + component buffs in the NecroL80 capture, frames 1644/1646/1648/1659, and proven byte-exact by
		// SpellweaveCodecTests). Three fields vary: the duration (centiseconds), the spell BASE id (= full id >> 8),
		// and a per-application effect NONCE; the rest are capture-derived constants (two 0x00010000 flags + two 0x50
		// magnitude words). The generic 50-byte Scythe-Shield blob the shared ApplySpell default uses is the WRONG
		// shape - the client's MagicBuffIIR_c::ReadSubClass runs off the end and rejects the buff ("Blob data may be
		// inconsistent, data invalid"), so nothing renders.
		//
		// `durationCentiseconds` is the spell's DurationMs / 10 (the client counts it down and ApplySpell reads it
		// back for expiry); `effectNonce` is a per-application unique id (so distinct buffs don't collapse onto one
		// effect instance client-side).
		public static byte[] BuildBuffData(uint spellBaseId, uint durationCentiseconds, uint effectNonce)
		{
			return new FaolanStream()
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(durationCentiseconds)
				.WriteUInt32(0x00010000)
				.WriteUInt32(0x00010000)
				.WriteUInt16(0x0000)
				.WriteUInt32(spellBaseId) // high byte 0 (base id < 0x1000000)
				.WriteUInt32(0x00000050)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(0x00000050)
				.WriteUInt32(0)
				.WriteUInt32(effectNonce)
				.ToArray();
		}

		// One fire-burst stage (StatIIR 0xF98E10B3) emitted per stage on DEACTIVATE: the accumulated weave firing.
		// Record: [op][0xC350][caster][u8 0x01][u32 fireId][u32 0x00007BD4]. Necro fire ids: 0x398/0x39B/0x39A/0x39D.
		// The 0x7BD4 tail is constant across the captured burst.
		public static byte[] BuildFireStage(uint caster, uint fireId)
		{
			return new FaolanStream().WriteUInt32(GameServer0X2000Opcodes.StatIIR).WriteUInt32(Type).WriteUInt32(caster).WriteByte(0x01).WriteUInt32(fireId).WriteUInt32(0x00007BD4).ToArray();
		}

		// The reactive element list (CombatReactiveIIR 0x54C02089): declares to the client which weave elements the
		// caster can ADD while in the trance - this is what lets the client send the 0x03-tagged adds. componentIndex
		// on an add indexes INTO this list. Record: [op][0xC350][caster][u32 0][u8 0x01][N x u32 elementBaseId].
		// elementBaseId = full spell id >> 8 (necro: Arcane Renewal 0x396AE9, Arcane Surge 0x3969D8, Parasite Host
		// 0x37E6D9, Death God 0x396A22, Death 0x396A3D). A per-add re-send zeroes the just-used element (cooldown);
		// pass 0 for a consumed slot to reproduce that. tag 0x01 is a constant "list active" flag.
		public static byte[] BuildReactiveList(uint caster, uint[] elementBaseIds)
		{
			var stream = new FaolanStream().WriteUInt32(GameServer0X2000Opcodes.CombatReactiveIIR).WriteUInt32(Type).WriteUInt32(caster).WriteUInt32(0).WriteByte(0x01);

			foreach (var element in elementBaseIds)
				stream.WriteUInt32(element);

			return stream.ToArray();
		}

		public static byte[] BuildStatGauge(uint caster, uint token, int delta, uint gaugeId, uint scale)
		{
			return new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.StatDamageIIR)
				.WriteUInt32(Type)
				.WriteUInt32(caster)
				.WriteByte(0x00)
				.WriteUInt32(token)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(scale)
				.WriteUInt32(2) // mode
				.WriteUInt32(delta) // signed drain since last tick
				.WriteUInt32(0)
				.WriteUInt32(Type)
				.WriteUInt32(caster)
				.WriteUInt32(1)
				.WriteUInt32(0)
				.WriteUInt32(gaugeId)
				.WriteUInt32(GaugeMagnitude)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.ToArray();
		}
	}
}
