using System;
using Faolan.GameServer.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact gates for the combat-effect IIR builders (#274), decoded from the guardian combat capture
	// (Blablabla_..._random-actions-combat). The captured carrier CONTENTS (without the leading [u32 length], which
	// the live sender adds via WriteArrayUInt32Length) must be reproduced from their typed fields.
	public class CombatEffectBuilderTests
	{
		// DamageResultIIR (0xFEC4158A), frame 6514: NPC 0x00002C56 hits player 0x01316D8A for 83 (type 2).
		[Fact]
		public void DamageResult_IsByteIdentical()
		{
			var captured = Convert.FromHexString("fec4158a0000c35001316d8a000000c35000002c5600000053000000020000000000000000");

			var built = CombatEffectBuilder.BuildDamageResult(0x01316D8A, 0x00002C56, 83, 2);

			Assert.Equal(captured, built);
		}

		// StatDamageIIR (0xE9F24184), frame 6689: Rend (base 0x316449) cast by the trapper 0x00002C58 applies -8 to
		// the player's stat 1; statValue (resulting value) 0x2FFA, effect slot idx 1.
		[Fact]
		public void StatDamage_IsByteIdentical()
		{
			var captured = Convert.FromHexString("e9f241840000c35001316d8a0000002ffa0000000000000000000000000000001b00000002" + "fffffff8000000010000c35000002c5800000001000000000031644900000001000000000000000000000000");

			var built = CombatEffectBuilder.BuildStatDamage(0x01316D8A, 0x2FFA, -8, 1, 0x00002C58, 1, 0x316449);

			Assert.Equal(captured, built);
		}
	}
}
