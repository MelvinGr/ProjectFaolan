using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Faolan.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// The fatality burst (#140), locked against the captured bytes. Long believed un-pinnable - the RE notes concluded
	// no capture showed a fatality - which held only for MELEE finishers; the corpus carries the SERVER side of a
	// caster's fatality in seven captures. Worked example: xxx_mage-necromancer beach->city fr1267, where the killing
	// cast's completion is followed, in one frame, by both Fatality self-buffs and (because that character had Frozen
	// Hatred up) its proc.
	public class FatalityBurstTests
	{
		// The three captured ApplySpell payloads of that burst, verbatim (the bytes AFTER the ApplySpell header).
		private const string FatalityAPayload = "00000000000000000000000a00010000000100000000004d5fcd0000000100000000000000000000000000000001000000001436da64";
		private const string FatalityBPayload = "0000000000000000000003e800010000000100000000000c12b500000001000000000000000000000000000000010000000014370110";
		private const string FrozenHatredProcPayload = "000000000000000000000258000100000001000000000037f0b30000000100000000000000000000000000000001000000001436f170";

		// The third buff of the burst is not a fatality CONSTANT any more - it is what Frozen Hatred arms on a fatality,
		// a SpellEffect row (#439). Named here only so the captured payload above can be checked against it;
		// SpellEffectSeedTests owns the assertion that this is what the seed ships.
		private const uint FrozenHatredProc = 3666099;

		// Every captured fatality payload is the ordinary 54-byte self-buff form: its own duration, a per-application
		// nonce, and the caster's LEVEL in the @26/@42 words - 1 here, because this burst is the first fatality of a
		// fresh character's beach run (the same words walk 1->2->3->4->5 as it levels, and read 80 in the L80 sessions;
		// see SelfBuffCodec, #438). Nothing about a fatality needs a new wire shape - which is why the burst is three
		// ApplySpell sends and not a bespoke record.
		[Theory]
		[InlineData(FatalityService.FatalityBuffA, 10u, 0x1436DA64u, FatalityAPayload)] // 0.1s
		[InlineData(FatalityService.FatalityBuffB, 1000u, 0x14370110u, FatalityBPayload)] // 10s
		[InlineData(FrozenHatredProc, 600u, 0x1436F170u, FrozenHatredProcPayload)] // 6s
		public void FatalityBuffPayload_MatchesCapture(uint spellBase, uint durationCs, uint nonce, string expected)
		{
			var payload = MagicBuffDataCodec.Build(spellBase, durationCs, nonce, 1, casterIsPlayer: true);

			Assert.Equal(expected, Convert.ToHexStringLower(payload));
		}

		// The ids the burst carries, so a rename/typo in the service is caught here rather than in-client.
		[Fact]
		public void FatalityBurst_CarriesTheCapturedSpellIds()
		{
			Assert.Equal(5070797u, FatalityService.FatalityBuffA);
			Assert.Equal(791221u, FatalityService.FatalityBuffB);
		}

		// The roll, from the client's own fatality groups (RDB 1010219): fatalitychance is a PERCENT and its tiers map
		// 1:1 onto minlevel - 1 -> 5%, 5 -> 10%, 30 -> 15%, 40/60 -> 20%, 80 -> 30% across the 58 groups / 205 real
		// entries. A character rolls the best tier its level qualifies for.
		[Theory]
		[InlineData(1u, 5)]
		[InlineData(4u, 5)]
		[InlineData(5u, 10)]
		[InlineData(29u, 10)]
		[InlineData(30u, 15)]
		[InlineData(40u, 20)]
		[InlineData(60u, 20)]
		[InlineData(79u, 20)]
		[InlineData(80u, 30)]
		public void FatalityChance_FollowsTheClientTiers(uint level, int expected)
		{
			Assert.Equal(expected, FatalityService.FatalityChancePercent(level));
		}

		// ⛔ A Fatality row's 53051 elements are a candidate REGISTRY, not #784's "when this lands, also run X", so the
		// burst applies the two ids ALONE. `791221`'s five edges are `Frozen Hatred`, `Companion Spirit`,
		// `Furious Inspiration`, `XX Daily Fatality Counter` and a second `Fatality` row - three different classes'
		// procs, a dev counter and a sibling carrier.
		//
		// The capture is what settles it: all 36 of its fatality bursts carry the two Fatality ids and only 21 carry
		// the Frozen Hatred proc, so the edges cannot be applied wholesale - the other four would be in all 36 and are
		// in none. Which one rides is the KILLER's state, which `ForFatality` resolves (#439).
		//
		// Walking them handed a level-80 NECROMANCER `Companion Spirit`, an ungated `Frozen Hatred` she had not armed,
		// and all TEN ranks of the class-44 `Furious Inspiration` at once - eleven icons expiring together 15 s later.
		[Fact]
		public async Task FatalityBurst_AppliesTheTwoIdsAloneAndNeverWalksTheirChain()
		{
			var outbound = new RecordingSelfBuffs();
			var service = new FatalityService(NullLogger.Instance, outbound, new SpellEffectService(null!, outbound, NullLogger.Instance));
			var client = new RecordingNetworkClient(0x01000001);
			client.Session.Character.Level = 80; // the 30% tier, so 200 rolls miss with probability ~1e-31
			var victim = new WorldEntity(0x01400001, 4010, false, "Scavenger Lackey", null!);

			for (var i = 0; i < 200; i++)
				await service.OnKill(client, null, victim);

			Assert.NotEmpty(outbound.Applied); // the roll fired at least once, so the assertions below are not vacuous
			Assert.All(outbound.Applied, a => Assert.False(a.WalkChain));
			Assert.All(outbound.Applied, a => Assert.Contains(a.WireSpellId >> 8, new[] { FatalityService.FatalityBuffA, FatalityService.FatalityBuffB }));
		}

		// ...and the walk it is given refuses everything, so the suppression holds for the carrier's whole sub-tree
		// rather than only for its first edge - an edge cut at the top must not be reachable through a second one
		// further down.
		[Fact]
		public void ARegistryWalk_ClaimsNothingAndRefusesEveryEdge()
		{
			var walk = SpellChainWalk.Registry();

			Assert.False(walk.TryEnter(791221)); // the carrier's own seeding call
			Assert.False(walk.TryEnter(299074)); // a first-level edge
			Assert.False(walk.TryEnter(299064)); // and one below it
			Assert.False(walk.Truncated); // refusing is not the fan-out cap biting
		}

		private sealed record AppliedBuff(uint WireSpellId, bool WalkChain);

		private sealed class RecordingSelfBuffs : ISpellEffectOutbound
		{
			public List<AppliedBuff> Applied { get; } = [];

			public Task ApplySelfBuff(INetworkClient client, uint wireSpellId, bool walkChain = true)
			{
				Applied.Add(new AppliedBuff(wireSpellId, walkChain));
				return Task.CompletedTask;
			}

			public Task<bool> SummonPet(INetworkClient client, string template, bool combat) => Task.FromResult(false);
			public Task SummonTimedPet(INetworkClient client, string template, TimeSpan duration, TimeSpan? cap) => Task.CompletedTask;
		}
	}
}
