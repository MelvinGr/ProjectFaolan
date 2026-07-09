using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// Byte-exact builders for the spellweaving activate-response + fire wire (docs/combos.md, decoded in
	// Captures/decode_spellweave.py from NecroL80_..._consuming-potions-food-spellweaving; caster 0x0802E5D4).
	// Each method returns the record bytes AFTER the 0x2000 carrier's u32 length prefix - the caller wraps them in
	// the world-framed IIR carrier (WriteArrayUInt32Length), exactly like PetIirCodec.
	//
	// The trance/component BUFF payloads use the generic 54-byte MagicBuffIIR form SelfBuffCodec.BuildBuffDataLong
	// builds (it is not spellweave-specific - the mount ride buff uses the same shape). This codec owns the records
	// that are genuinely spellweave: the reactive element LIST, the FIRE burst, and the stat GAUGE ticks.
	public static class SpellweaveCodec
	{
		// The IIR types this codec constructs or parses (#317 convention; read by the coverage matrix).
		public static readonly GameServer0X2000Opcodes[] HandledTypes =
		[
			GameServer0X2000Opcodes.CombatReactiveIIR,
			GameServer0X2000Opcodes.StatDamageIIR,
			GameServer0X2000Opcodes.StatIIR,
		];

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

		// One fire-burst stage (StatIIR 0xF98E10B3) emitted per stage on DEACTIVATE: the accumulated weave firing.
		// Record: [op][0xC350][caster][u8 0x01][u32 fireId][u32 0x00007BD4]. Necro fire ids: 0x398/0x39B/0x39A/0x39D.
		// The 0x7BD4 tail is constant across the captured burst.
		public static byte[] BuildFireStage(uint caster, uint fireId)
		{
			return new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.StatIIR)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(caster)
				.WriteByte(0x01)
				.WriteUInt32(fireId)
				.WriteUInt32(0x00007BD4)
				.ToArray();
		}

		// The reactive element list (CombatReactiveIIR 0x54C02089): declares to the client which weave elements the
		// caster can ADD while in the trance - this is what lets the client send the 0x03-tagged adds. componentIndex
		// on an add indexes INTO this list. Record: [op][0xC350][caster][u32 0][u8 0x01][N x u32 elementBaseId].
		// elementBaseId = full spell id >> 8 (necro: Arcane Renewal 0x396AE9, Arcane Surge 0x3969D8, Parasite Host
		// 0x37E6D9, Death God 0x396A22, Death 0x396A3D). A per-add re-send zeroes the just-used element (cooldown);
		// pass 0 for a consumed slot to reproduce that. tag 0x01 is a constant "list active" flag.
		public static byte[] BuildReactiveList(uint caster, uint[] elementBaseIds)
		{
			var stream = new FaolanStream().WriteUInt32(GameServer0X2000Opcodes.CombatReactiveIIR).WriteUInt32(EntityClass.Character).WriteUInt32(caster).WriteUInt32(0).WriteByte(0x01);

			foreach (var element in elementBaseIds)
				stream.WriteUInt32(element);

			return stream.ToArray();
		}

		public static byte[] BuildStatGauge(uint caster, uint token, int delta, uint gaugeId, uint scale)
		{
			return new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.StatDamageIIR)
				.WriteUInt32(EntityClass.Character)
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
				.WriteUInt32(EntityClass.Character)
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
