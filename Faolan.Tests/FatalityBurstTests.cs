using System;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.GameServer.Combat;
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

		// Every captured fatality payload is the ordinary 54-byte self-buff form: its own duration, magnitude 1, and a
		// per-application nonce. Nothing about a fatality needs a new wire shape - which is why the burst is three
		// ApplySpell sends and not a bespoke record.
		[Theory]
		[InlineData(FatalityService.FatalityBuffA, 10u, 0x1436DA64u, FatalityAPayload)] // 0.1s
		[InlineData(FatalityService.FatalityBuffB, 1000u, 0x14370110u, FatalityBPayload)] // 10s
		[InlineData(FatalityService.FrozenHatredProc, 600u, 0x1436F170u, FrozenHatredProcPayload)] // 6s
		public void FatalityBuffPayload_MatchesCapture(uint spellBase, uint durationCs, uint nonce, string expected)
		{
			var payload = SelfBuffCodec.BuildBuffDataLong(spellBase, durationCs, nonce, 1);

			Assert.Equal(expected, Convert.ToHexStringLower(payload));
		}

		// The ids the burst carries, so a rename/typo in the service is caught here rather than in-client.
		[Fact]
		public void FatalityBurst_CarriesTheCapturedSpellIds()
		{
			Assert.Equal(5070797u, FatalityService.FatalityBuffA);
			Assert.Equal(791221u, FatalityService.FatalityBuffB);
			Assert.Equal(3666099u, FatalityService.FrozenHatredProc);

			// The proc keys off the buff the CASTABLE Frozen Hatred (4377207) applies - not off the castable itself.
			Assert.Equal(4377209u, FatalityService.FrozenHatredBuff);
		}

		// The tooltip's rule, as constants: 60s per fatality, capped 5 minutes out.
		[Fact]
		public void FrozenHatredPet_LivesSixtySecondsCappedAtFive()
		{
			Assert.Equal(TimeSpan.FromSeconds(60), FatalityService.FrozenHatredPetDuration);
			Assert.Equal(TimeSpan.FromMinutes(5), FatalityService.FrozenHatredPetCap);
		}
	}
}
