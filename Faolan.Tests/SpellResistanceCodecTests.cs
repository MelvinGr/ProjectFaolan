using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Constants;
using Xunit;

namespace Faolan.Tests
{
	// Spell resistance. `--report method-bodies --report-arg Resistance` is the oracle: 17 frames over 5
	// captures, every one of them eight bytes, and only two distinct metatypes.
	public class SpellResistanceCodecTests
	{
		// The two captured bodies, verbatim. Both directions carry the same pair - which is why one codec serves
		// both methods.
		private static readonly byte[] CriminalTeamBody = [0x00, 0x00, 0x07, 0xD5, 0x00, 0x00, 0x00, 0x64];
		private static readonly byte[] KnockbackBody = [0x00, 0x00, 0x01, 0xAB, 0x00, 0x00, 0x00, 0x64];

		[Fact]
		public void CriminalTeamResistance_RoundTripsTheCapturedBody()
		{
			var parsed = SpellResistanceCodec.Parse(CriminalTeamBody);

			Assert.Equal(SpellMetaTypes.CriminalTeam, parsed.MetaType);
			Assert.Equal(SpellMetaTypes.FullImmunity, parsed.Percentage);
			Assert.Equal(CriminalTeamBody, SpellResistanceCodec.Build(parsed));
		}

		[Fact]
		public void KnockbackResistance_RoundTripsTheCapturedBody()
		{
			var parsed = SpellResistanceCodec.Parse(KnockbackBody);

			Assert.Equal(SpellMetaTypes.Knockback, parsed.MetaType);
			Assert.Equal(SpellMetaTypes.FullImmunity, parsed.Percentage);
			Assert.Equal(KnockbackBody, SpellResistanceCodec.Build(parsed));
		}

		// The add and the remove are the same layout with the verb in the method - the `HandledMethods` case. If a
		// future edit picked one of the two as *the* Method, it would be a lie about the other.
		[Fact]
		public void BothVerbsShareOneBody()
		{
			Assert.Equal(
			[
				GameCharInterfaceMethods.DispatchAddSpellResistance,
				GameCharInterfaceMethods.DispatchRemoveSpellResistance
			], SpellResistanceCodec.HandledMethods);

			Assert.Equal(CriminalTeamBody, SpellResistanceCodec.Build(SpellMetaTypes.CriminalTeam, SpellMetaTypes.FullImmunity));
		}
	}
}
