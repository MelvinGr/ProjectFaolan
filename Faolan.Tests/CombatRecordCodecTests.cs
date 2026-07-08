using System;
using Faolan.Core.Network.Codecs.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact construct + round-trip for the server->client combat-state records (#216/#218,
	// docs/iir-readers-combat-world.md): CombatShield, ThreatLevel, HateListUpdate, StatDamage. Oracles are captured
	// bodies (full record incl. the 4-byte length prefix).
	public class CombatRecordCodecTests
	{
		private const string CombatShieldBody = "00000011cffd05190000c35000017f80000000006f";

		private const string HateListBody = "0000001a8a01e5a10000c35001316e7100000000c35000017f8000000001";

		private const string StatDamageBody = "00000051e9f241840000c35000017f8000000014cf0000000000000000000000000000001b00000002fffffc33000000040000c35001316e710000000100000000003006fe00000001000000000000000000000000";

		private const string ThreatLevelBody = "000000121745ef6c0000c35001316e71000000000001";

		[Fact]
		public void CombatShield_BuildsByteExact()
		{
			var p = CombatShieldCodec.Parse(Convert.FromHexString(CombatShieldBody));
			Assert.Equal(0x00017F80u, p.Handle);
			Assert.Equal(0x6Fu, p.Value);
			Assert.Equal(CombatShieldBody, Convert.ToHexStringLower(CombatShieldCodec.Build(p.Handle, p.Value, p.Env)));
		}

		[Fact]
		public void HateList_BuildsByteExact()
		{
			var p = HateListCodec.Parse(Convert.FromHexString(HateListBody));
			Assert.Equal(0x01316E71u, p.Handle); // the hater
			Assert.Equal(0x00017F80u, p.Target); // who it hates
			Assert.Equal(1u, p.Count);
			Assert.Equal(HateListBody, Convert.ToHexStringLower(HateListCodec.Build(p.Handle, p.Target, p.Count, p.Flag, p.Env)));
		}

		[Fact]
		public void StatDamage_RoundTripsByteExact()
		{
			var captured = Convert.FromHexString(StatDamageBody);
			var p = StatDamageCodec.Parse(captured);
			Assert.Equal(0x00017F80u, p.Handle); // the target
			Assert.Equal(StatDamageCodec.WordCount, p.Words.Length);
			Assert.Contains(0x01316E71u, p.Words); // the attacker handle rides as a word
			Assert.Equal(StatDamageBody, Convert.ToHexStringLower(StatDamageCodec.Build(p.Handle, p.Words, p.Env)));
		}

		[Fact]
		public void ThreatLevel_BuildsByteExact()
		{
			var p = ThreatLevelCodec.Parse(Convert.FromHexString(ThreatLevelBody));
			Assert.Equal(0x01316E71u, p.Handle);
			Assert.Equal(1, p.Flag);
			Assert.Equal(ThreatLevelBody, Convert.ToHexStringLower(ThreatLevelCodec.Build(p.Handle, p.Threat, p.Flag, p.Env)));
		}
	}
}
