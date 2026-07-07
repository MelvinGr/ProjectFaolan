using System;
using Faolan.Core.Network.Codecs;
using Xunit;

namespace Faolan.Tests
{
	// Build/Parse golden tests for the small combat result packets (DamageResultIIR 0xFEC4158A, EnergyIIR 0x40749B8A).
	// The layouts are capture-derived (strickenbla combat capture; see protocol_stats_and_combat) and were previously
	// built inline in two places each in CombatService - now the single CombatPacketCodec (#89).
	public class CombatPacketCodecTests
	{
		private const uint Attacker = 0x021663DD; // the captured character instance
		private const uint Target = 0x00017E29;

		[Fact]
		public void DamageResultBody_MatchesExpectedLayout()
		{
			var body = CombatPacketCodec.BuildDamageResultBody(Attacker, Target, 0x2A);

			Assert.Equal(CombatPacketCodec.DamageResultBodyLength, body.Length);
			Assert.Equal("fec4158a0000c350021663dd000000c35000017e290000002a000000020000000000000000", Convert.ToHexStringLower(body));
		}

		[Theory, InlineData(0u), InlineData(42u), InlineData(0xFFFFFFFFu)]
		public void DamageResultBody_RoundTrips(uint damage)
		{
			var body = CombatPacketCodec.BuildDamageResultBody(Attacker, Target, damage);

			Assert.True(CombatPacketCodec.TryParseDamageResultBody(body, out var attacker, out var target, out var parsedDamage));
			Assert.Equal(Attacker, attacker);
			Assert.Equal(Target, target);
			Assert.Equal(damage, parsedDamage);
		}

		[Fact]
		public void EnergyBody_MatchesExpectedLayout()
		{
			var body = CombatPacketCodec.BuildEnergyBody(Attacker, 0x189C); // stamina 6300

			Assert.Equal(CombatPacketCodec.EnergyBodyLength, body.Length);
			Assert.Equal("40749b8a0000c350021663dd010000189c", Convert.ToHexStringLower(body));
		}

		[Theory, InlineData(0u), InlineData(6300u)]
		public void EnergyBody_RoundTrips(uint stamina)
		{
			var body = CombatPacketCodec.BuildEnergyBody(Attacker, stamina);

			Assert.True(CombatPacketCodec.TryParseEnergyBody(body, out var actor, out var parsedStamina));
			Assert.Equal(Attacker, actor);
			Assert.Equal(stamina, parsedStamina);
		}

		// The NPC counter-attack animation ack (BehaviorControlCenterIIR 0x642CD3D6 with actor = NPC handle), byte-exact
		// against frame 6472 of the strickenbla combat capture (NPC 0x2c56 swings at player 0x01316d8a for 0x53). The
		// expected hex is the captured 0x2000 body MINUS its 4-byte totalLen prefix (the builder produces the inner
		// record that WriteArrayUInt32Length wraps). Decode: Captures/scripts/decode_combat_npc_ack.py.
		[Fact]
		public void NpcAttackAnimationAck_MatchesCapturedFrame6472()
		{
			var body = CombatPacketCodec.BuildNpcAttackAnimationAckBody(0x00002C56, 0x01316D8A, 0x53, 49731, 3, 24, 67, 60, 0xCD);

			Assert.Equal("642cd3d60000c35000002c5600040000000f0000001908c38403180328183000520908d08603108adbc5095843603c" + "000000000000000000000001000000000000000000000000000000000000000000000000000000010000c35001316d8a" +
			             "0000000006000000cd000000000000000100000053000000020000000000000000",
				Convert.ToHexStringLower(body));
		}

		[Fact]
		public void TryParseDamageResultBody_RejectsMalformed()
		{
			var body = CombatPacketCodec.BuildDamageResultBody(Attacker, Target, 1);
			Assert.False(CombatPacketCodec.TryParseDamageResultBody(new byte[36], out _, out _, out _)); // wrong length
			var corrupt = (byte[])body.Clone();
			corrupt[0] ^= 0xFF; // sub-opcode
			Assert.False(CombatPacketCodec.TryParseDamageResultBody(corrupt, out _, out _, out _));
			var corruptConst = (byte[])body.Clone();
			corruptConst[25] ^= 0xFF; // the trailing 0x02 constant
			Assert.False(CombatPacketCodec.TryParseDamageResultBody(corruptConst, out _, out _, out _));
		}

		[Fact]
		public void TryParseEnergyBody_RejectsMalformed()
		{
			var body = CombatPacketCodec.BuildEnergyBody(Attacker, 1);
			Assert.False(CombatPacketCodec.TryParseEnergyBody(new byte[16], out _, out _)); // wrong length
			var corrupt = (byte[])body.Clone();
			corrupt[12] = 0x00; // marker must be 0x01
			Assert.False(CombatPacketCodec.TryParseEnergyBody(corrupt, out _, out _));
		}
	}
}
