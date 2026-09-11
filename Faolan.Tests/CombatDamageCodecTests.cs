using System;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Progression;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact gates for the two combat damage IIRs, decoded from the guardian combat capture
	// (Blablabla_..._random-actions-combat). The captured carrier contents (without the leading [u32 length], which
	// the live sender adds via WriteArrayUInt32Length) must be reproduced from their typed fields.
	public class CombatDamageCodecTests
	{
		// DamageResultIIR (0xFEC4158A), frame 6514: NPC 0x00002C56 hits player 0x01316D8A for 83; that NPC's damage
		// school is Piercing (wire type 2).
		[Fact]
		public void DamageResult_IsByteIdentical()
		{
			var captured = Convert.FromHexString("fec4158a0000c35001316d8a000000c35000002c5600000053000000020000000000000000");

			var built = DamageResultCodec.Build(0x01316D8A, 0x00002C56, 83, DamageSchool.Piercing);

			Assert.Equal(captured, built);

			var rt = DamageResultCodec.Parse(built);
			Assert.Equal(0x01316D8Au, rt.Target);
			Assert.Equal(0x00002C56u, rt.Attacker);
			Assert.Equal(83u, rt.Damage);
			Assert.Equal(DamageSchool.Piercing, rt.School);
			Assert.Equal(built, DamageResultCodec.Build(rt));
		}

		// The environmental form of StatDamageIIR - fall damage. Golden:
		// NecroL80_mage-necromancer_login_gameplay_chat-annotated-3 frame 11451 (19:43:30.109), the 11.9-unit fall that
		// also applied Bruised Body. Stat 0x1B (Health), school 12, and every caster/spell word zero.
		//
		// This being a separate codec from StatDamage is the point of the test: the combat form's `statValue` is the
		// target's resulting health and its count word is 2, while here the first word is the damage magnitude (219125 =
		// 2191.25 health), the delta is exactly its negation, and the count word is 0. Building this with the combat
		// builder would put an EntityClass marker at word 8 and a 1 at word 13, neither of which retail sends.
		[Fact]
		public void EnvironmentalDamage_IsByteIdentical()
		{
			var captured = Convert.FromHexString(
				"e9f241840000c3500802e5d400000357f50000000000000000000000000000001b00000000fffca80b0000000c000000000000000000000000000000000000000000000000000000000000000000000000");

			var built = StatDamageCodec.Build(StatDamages.Environmental(0x0802E5D4, 219125, (uint)CharacterStatId.HealthCurrent, FallDamage.EnvironmentalSchool));

			Assert.Equal(captured, built);

			var rt = StatDamageCodec.Parse(built);
			Assert.Equal(0x0802E5D4u, rt.Handle);
			Assert.Equal(219125u, rt.StatValue);
			Assert.Equal(-219125, rt.Delta);
			Assert.Equal(0u, rt.Count); // the combat form's 2 - the tell that there is no caster
			Assert.Equal((uint)CharacterStatId.HealthCurrent, rt.StatId);
			Assert.Equal(FallDamage.EnvironmentalSchool, (uint)rt.School);
			Assert.Equal(built, StatDamageCodec.Build(rt));
		}

		/// <summary>
		///     The environmental record's amount is a wire value, and the sender is what converts it.
		/// </summary>
		/// <remarks>
		///     <b>The bug this pins was visible in game and invisible here.</b> `EnvironmentService` works in display
		///     units, like everything on `EntityVitals`, and handed them straight to the record while the vitals push
		///     beside it scaled through `CharacterStatBlock.Set`. So a fall moved the health bar by the right amount
		///     and rendered a floating number a hundredth of it - "1" for a fall that cost 27 health.
		///     <para />
		///     The golden above is retail's own frame and could not catch it: it builds the record from an
		///     already-scaled 219125 and says nothing about who scales. This asserts the conversion instead, which is
		///     where the two halves of a fall disagreed.
		/// </remarks>
		[Fact]
		public void EnvironmentalDamage_ScalesTheDisplayAmountToTheWire()
		{
			// 2191 health lost reaches the wire as 219100 - the same hundredths the captured frame carries for its
			// 2191.25.
			Assert.Equal(219100u, EnvironmentOutbound.WireAmount(2191, (uint)CharacterStatId.HealthCurrent));

			// And it is the stat's own scale, not a flat 100: the same table the vitals push goes through.
			Assert.Equal(StatDisplayScale.Of((uint)CharacterStatId.HealthCurrent),
				EnvironmentOutbound.WireAmount(1, (uint)CharacterStatId.HealthCurrent));
		}

		// StatDamageIIR (0xE9F24184), frame 6689: Rend (base 0x316449) cast by the trapper 0x00002C58 applies -8 to
		// the player's stat 1; statValue (resulting value) 0x2FFA, effect slot idx 1.
		[Fact]
		public void StatDamage_IsByteIdentical()
		{
			var captured = Convert.FromHexString(
				"e9f241840000c35001316d8a0000002ffa0000000000000000000000000000001b00000002fffffff8000000010000c35000002c5800000001000000000031644900000001000000000000000000000000");

			var built = StatDamageCodec.Build(StatDamages.Combat(0x01316D8A, 0x2FFA, -8, DamageSchool.Slashing, 0x00002C58, 1, 0x316449));

			Assert.Equal(captured, built);

			// The signed delta survives the round-trip.
			var rt = StatDamageCodec.Parse(built);
			Assert.Equal(-8, rt.Delta);
			Assert.Equal(0x316449u, rt.SpellBaseId);
			Assert.Equal(built, StatDamageCodec.Build(rt));
		}
	}
}
