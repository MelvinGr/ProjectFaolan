using System.Threading.Tasks;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Protocol;
using Faolan.GameServer.Combat;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// `StatDamageIIR.Delta` is the only field that moves a health bar, and it must be on the client's ×100 vitals
	// scale. The client's apply is `StatDamageIIR_t` vtable slot 2, `FUN_008d5960`, and its whole stat write is
	//
	//     SetStat(statId, GetStat(statId) + record.delta)          // 008d5b2d..008d5b5d
	//
	// so `StatValue` is never read - the client recomputes from what it already holds - and a delta stated in display
	// units moves a wire-scale pool by a hundredth of the damage dealt.
	//
	// A 12-damage hit takes a 20,200-wire bar (202 HP) to 20,188, the client redraws 201.88 as 202, and the bar looks
	// frozen while the character really does die. `RegenIIR` has the ×100 right, so the only correction the client
	// ever gets is upward and the bar climbs past its own max - the `688 / 202` second symptom.
	//
	// The two fields are what make this testable at all: nothing in the packet says "×100", but the corpus pins the
	// two to each other - `StatValue` equals the same handle's `StatUpdateIIR` stat 27 exactly (diff 0 on twelve
	// handles, up to 92 records each) and moves by exactly `Delta` across 1,015 consecutive pairs. So a record whose
	// two numbers are on different scales is self-contradictory regardless of which scale is right, and that is the
	// invariant here. It held for three weeks with `StatValue` scaled and `Delta` not, because a golden byte test
	// asserts the bytes we chose and this asserts a relation between them.
	public class StatDamageWireScaleTests
	{
		private const uint Instance = 0x0802E5D4;
		private const uint Caster = 0x01316D8A;

		// The ×100 the client keeps vitals in (`StatDisplayScale`; in-client, wire 428500 draws as 4285 HP).
		private const int VitalsScale = 100;

		[Theory]
		[InlineData(137u, 12)] // an ordinary NPC swing on a starter pool
		[InlineData(6518u, 68)] // a level-80 necromancer, the corpus's own player handle
		[InlineData(202u, 1)] // the smallest hit that must still register - a floored DoT tick
		public async Task SendStatDamage_StatesHealthAndDeltaOnTheSameWireScale(uint displayHealth, int displayDamage)
		{
			var record = await Emit(displayHealth, -displayDamage);

			Assert.Equal(displayHealth * (uint)VitalsScale, record.StatValue);
			Assert.Equal(-displayDamage * VitalsScale, record.Delta);
		}

		// The relation, stated without either constant: applying the delta to the health the previous record announced
		// has to land on the health this one announces. That is literally what the client computes, so a scale slip on
		// either field breaks it - and it stays true if the vitals scale ever turns out to be something other than 100.
		[Fact]
		public async Task ApplyingTheDeltaToThePreviousRecordsHealthReachesThisRecordsHealth()
		{
			const uint before = 137;
			const int damage = 12;

			var first = await Emit(before, 0);
			var hit = await Emit(before - (uint)damage, -damage);

			Assert.Equal((long)first.StatValue + hit.Delta, hit.StatValue);
		}

		// A heal-over-time is the same record with the sign flipped, and the scale must not be applied to the magnitude
		// only - a positive delta that lost its ×100 would trickle a bar upward at a hundredth of the rate.
		[Fact]
		public async Task APositiveDeltaIsScaledToo()
		{
			var record = await Emit(140, +3);

			Assert.Equal(3 * VitalsScale, record.Delta);
		}

		// Through the real sender and the real framing, so the test cannot pass on a value the wire never carries.
		private static async Task<StatDamageIIR> Emit(uint displayHealth, int displayDelta)
		{
			var client = new RecordingNetworkClient(Instance);
			await CombatService.SendStatDamage(client, Instance, displayHealth, displayDelta, (uint)DamageSchool.Generic, Caster, 0x316449);

			// The mirror of WorldIir.Send's WriteArrayUInt32Length - the carrier length belongs to the sender, so it
			// is read off here and never inside the codec.
			return StatDamageCodec.Parse(Rpc.Parse(client.Sent[^1]).BodyReader().ReadArrayUInt32Length());
		}
	}
}
