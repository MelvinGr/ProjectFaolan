using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Build/Parse golden tests for the small combat result packets (EnergyIIR 0x40749B8A, DamageResultIIR 0xFEC4158A)
	// and the NPC attack ack. The layouts are capture-derived (strickenbla combat capture; see
	// protocol_stats_and_combat); #530 wave 4 split the one "CombatPacketCodec" that carried all three unrelated IIRs
	// into a codec each.
	//
	// ⚠️ The carrier handle of a DamageResultIIR is the TARGET and the second endpoint is the ATTACKER - the bytes
	// below are unchanged, but the names were the other way round while this file owned its own copy of the layout.
	public class EnergyCodecTests
	{
		private const uint Target = 0x021663DD; // the captured character instance
		private const uint Attacker = 0x00017E29;

		[Fact]
		public void DamageResultBody_MatchesExpectedLayout()
		{
			var body = DamageResultCodec.Build(Target, Attacker, 0x2A, DamageSchool.Piercing);

			Assert.Equal(37, body.Length);
			Assert.Equal("fec4158a0000c350021663dd000000c35000017e290000002a000000020000000000000000", Convert.ToHexStringLower(body));
		}

		[Theory, InlineData(0u), InlineData(42u), InlineData(0xFFFFFFFFu)]
		public void DamageResultBody_RoundTrips(uint damage)
		{
			var body = DamageResultCodec.Build(Target, Attacker, damage, DamageSchool.Piercing);

			var parsed = DamageResultCodec.Parse(body);
			Assert.Equal(Attacker, parsed.Attacker);
			Assert.Equal(Target, parsed.Target);
			Assert.Equal(damage, parsed.Damage);
			Assert.Equal(body, DamageResultCodec.Build(parsed));
		}

		[Fact]
		public void EnergyBody_MatchesExpectedLayout()
		{
			var body = EnergyCodec.Build(Target, 0x189C); // stamina 6300

			Assert.Equal(17, body.Length);
			Assert.Equal("40749b8a0000c350021663dd010000189c", Convert.ToHexStringLower(body));
		}

		[Theory, InlineData(0u), InlineData(6300u)]
		public void EnergyBody_RoundTrips(uint stamina)
		{
			var body = EnergyCodec.Build(Target, stamina);

			var parsed = EnergyCodec.Parse(body);
			Assert.Equal(Target, parsed.Actor);
			Assert.Equal(stamina, parsed.Stamina);
			Assert.Equal(body, EnergyCodec.Build(parsed));
		}

		// The NPC counter-attack animation ack (BehaviorControlCenterIIR 0x642CD3D6 with actor = NPC handle), byte-exact
		// against frame 6472 of the strickenbla combat capture (NPC 0x2c56 swings at player 0x01316d8a for 0x53). The
		// expected hex is the captured 0x2000 body MINUS its 4-byte totalLen prefix (the builder produces the inner
		// record that WriteArrayUInt32Length wraps). Decode: tools/capture/decode_combat_npc_ack.py.
		[Fact]
		public void NpcAttackAnimationAck_MatchesCapturedFrame6472()
		{
			var body = BehaviorControlCenterCodec.Build(BehaviorAnimations.NpcAttackAck(0x00002C56, 0x01316D8A, 0x53, 49731, 3, 24, 67, 60, 0xCD));

			Assert.Equal(
				"642cd3d60000c35000002c5600040000000f0000001908c38403180328183000520908d08603108adbc5095843603c" +
				"000000000000000000000001000000000000000000000000000000000000000000000000000000010000c35001316d8a" + "0000000006000000cd000000000000000100000053000000020000000000000000",
				Convert.ToHexStringLower(body));
		}

		[Fact]
		public void DamageResult_ParseRejectsMalformed()
		{
			var body = DamageResultCodec.Build(Target, Attacker, 1, DamageSchool.Piercing);
			Assert.Throws<CodecParseException>(() => DamageResultCodec.Parse(new byte[36])); // short
			var corrupt = (byte[])body.Clone();
			corrupt[0] ^= 0xFF; // sub-opcode
			Assert.Throws<CodecParseException>(() => DamageResultCodec.Parse(corrupt));
			var overlong = new byte[body.Length + 1];
			body.CopyTo(overlong, 0);
			Assert.Throws<CodecParseException>(() => DamageResultCodec.Parse(overlong));
		}

		[Fact]
		public void Energy_ParseRejectsMalformed()
		{
			Assert.Throws<CodecParseException>(() => EnergyCodec.Parse(new byte[16])); // short
			var corrupt = (byte[])EnergyCodec.Build(Target, 1).Clone();
			corrupt[4] ^= 0xFF; // the entity class word
			Assert.Throws<CodecParseException>(() => EnergyCodec.Parse(corrupt));
		}
	}
}
